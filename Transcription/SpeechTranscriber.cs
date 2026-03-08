using System.Collections.Concurrent;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using TeamsBot.Meetings;

namespace TeamsBot.Transcription;

/// <summary>
/// Per-meeting Azure Speech SDK session using PushAudioInputStream.
/// Receives raw 16kHz/16-bit/mono PCM from the RMP media session and
/// feeds it into Azure Speech for continuous recognition.
/// </summary>
public sealed class SpeechTranscriber : IAsyncDisposable
{
    private readonly IConfiguration _config;
    private readonly MeetingStore _store;
    private readonly ILogger<SpeechTranscriber> _logger;

    // meetingId → active recognizer session
    private readonly ConcurrentDictionary<string, RecognizerSession> _sessions = new();

    public SpeechTranscriber(
        IConfiguration config,
        MeetingStore store,
        ILogger<SpeechTranscriber> logger)
    {
        _config = config;
        _store  = store;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Session management
    // -----------------------------------------------------------------------

    /// <summary>Start a new recognition session for the given meeting.</summary>
    public void StartSession(string meetingId)
    {
        if (_sessions.ContainsKey(meetingId))
        {
            _logger.LogWarning("Speech session already exists for {MeetingId}", meetingId);
            return;
        }

        var speechConfig = SpeechConfig.FromSubscription(
            _config["AzureSpeechKey"]
                ?? throw new InvalidOperationException("AzureSpeechKey is not configured"),
            _config["AzureSpeechRegion"]
                ?? throw new InvalidOperationException("AzureSpeechRegion is not configured")
        );

        // Request detailed results including confidence scores
        speechConfig.OutputFormat = OutputFormat.Detailed;
        speechConfig.SetProperty(
            PropertyId.SpeechServiceResponse_RequestWordLevelTimestamps, "true");

        // 16 kHz / 16-bit / mono — MUST match what the RMP SDK delivers
        // (verified at runtime in RealTimeMediaSession.OnAudioMediaReceived)
        var pushStream  = AudioInputStream.CreatePushStream(
            AudioStreamFormat.GetWaveFormatPCM(sampleRate: 16000, bitsPerSample: 16, channels: 1));
        var audioConfig = AudioConfig.FromStreamInput(pushStream);
        var recognizer  = new SpeechRecognizer(speechConfig, audioConfig);

        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech
                && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                var confidence = TryGetConfidence(e.Result);
                var segment = new TranscriptSegment(
                    MeetingId:  meetingId,
                    Text:       e.Result.Text,
                    Language:   speechConfig.SpeechRecognitionLanguage,
                    Timestamp:  DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    Confidence: confidence,
                    SpeakerId:  null
                );
                _store.AddSegment(segment);
                _logger.LogInformation("[{MeetingId}] ✓ {Text}", meetingId, e.Result.Text);
            }
        };

        recognizer.Canceled += (_, e) =>
        {
            _logger.LogError(
                "[{MeetingId}] Speech recognition canceled — Reason: {Reason}, Code: {Code}, Details: {Details}",
                meetingId, e.Reason, e.ErrorCode, e.ErrorDetails);
        };

        recognizer.SessionStopped += (_, _) =>
            _logger.LogInformation("[{MeetingId}] Speech session stopped", meetingId);

        recognizer.StartContinuousRecognitionAsync().GetAwaiter().GetResult();

        var session = new RecognizerSession(recognizer, pushStream);
        _sessions[meetingId] = session;

        _logger.LogInformation("Speech recognition started for meeting {MeetingId}", meetingId);
    }

    /// <summary>Push raw PCM bytes into the speech recognizer for this meeting.</summary>
    public void PushAudio(string meetingId, byte[] pcmBytes)
    {
        if (_sessions.TryGetValue(meetingId, out var session))
        {
            session.PushStream.Write(pcmBytes, pcmBytes.Length);
        }
        else
        {
            _logger.LogWarning("No active speech session for meeting {MeetingId}", meetingId);
        }
    }

    /// <summary>Stop recognition and clean up for a meeting.</summary>
    public async Task StopSessionAsync(string meetingId)
    {
        if (!_sessions.TryRemove(meetingId, out var session))
            return;

        try
        {
            await session.Recognizer.StopContinuousRecognitionAsync();
        }
        finally
        {
            session.Recognizer.Dispose();
            session.PushStream.Close();
            _logger.LogInformation("Speech recognition stopped for meeting {MeetingId}", meetingId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (id, _) in _sessions)
            await StopSessionAsync(id);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static double TryGetConfidence(SpeechRecognitionResult result)
    {
        try
        {
            // Detailed output includes "NBest" array with confidence scores
            var json = result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult);
            if (!string.IsNullOrEmpty(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("NBest", out var nBest)
                    && nBest.GetArrayLength() > 0)
                {
                    return nBest[0].GetProperty("Confidence").GetDouble();
                }
            }
        }
        catch { /* Non-critical — fall through to default */ }

        return 0.9;
    }

    private sealed record RecognizerSession(SpeechRecognizer Recognizer, PushAudioInputStream PushStream);
}
