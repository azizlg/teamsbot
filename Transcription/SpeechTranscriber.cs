using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

    // meetingId → accumulated raw PCM for WAV recording
    private readonly ConcurrentDictionary<string, MemoryStream> _audioBuffers = new();

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
            AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
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
        _audioBuffers[meetingId] = new MemoryStream();

        _logger.LogInformation("Speech recognition started for meeting {MeetingId}", meetingId);
    }

    /// <summary>Push raw PCM bytes into the speech recognizer for this meeting.</summary>
    public void PushAudio(string meetingId, byte[] pcmBytes)
    {
        if (_sessions.TryGetValue(meetingId, out var session))
        {
            session.PushStream.Write(pcmBytes, pcmBytes.Length);
        }

        // Accumulate raw PCM for WAV recording regardless of speech session state
        if (_audioBuffers.TryGetValue(meetingId, out var buf))
        {
            lock (buf) { buf.Write(pcmBytes, 0, pcmBytes.Length); }
        }
    }

    /// <summary>Save accumulated PCM as a WAV file and remove the buffer.</summary>
    public async Task SaveAudioAsync(string meetingId)
    {
        if (!_audioBuffers.TryRemove(meetingId, out var buffer))
            return;

        byte[] pcmData;
        lock (buffer) { pcmData = buffer.ToArray(); buffer.Dispose(); }

        if (pcmData.Length == 0)
        {
            _logger.LogInformation("[{MeetingId}] No audio data captured", meetingId);
            return;
        }

        var dir = Path.Combine(AppContext.BaseDirectory, "recordings", meetingId);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "audio.wav");

        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        WriteWavHeader(fs, pcmData.Length, sampleRate: 16000, bitsPerSample: 16, channels: 1);
        await fs.WriteAsync(pcmData);

        _logger.LogInformation("[{MeetingId}] Audio saved: {Path} ({Bytes} bytes)", meetingId, filePath, pcmData.Length);
    }

    /// <summary>Save all active audio buffers (crash-safety flush).</summary>
    public async Task SaveAllActiveAudioAsync()
    {
        foreach (var meetingId in _audioBuffers.Keys.ToList())
        {
            try { await SaveAudioAsync(meetingId); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to save audio for {MeetingId}", meetingId); }
        }
    }

    /// <summary>Stop recognition and clean up for a meeting.</summary>
    public async Task StopSessionAsync(string meetingId)
    {
        // Save audio if not already saved (fallback for unexpected termination)
        if (_audioBuffers.ContainsKey(meetingId))
            await SaveAudioAsync(meetingId);

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
        await SaveAllActiveAudioAsync();
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

    private static void WriteWavHeader(Stream s, int pcmLength, int sampleRate, int bitsPerSample, int channels)
    {
        int byteRate   = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int riffSize   = 36 + pcmLength;

        using var bw = new BinaryWriter(s, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                        // fmt chunk size
        bw.Write((short)1);                  // PCM format
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(pcmLength);
    }

    private sealed record RecognizerSession(SpeechRecognizer Recognizer, PushAudioInputStream PushStream);
}
