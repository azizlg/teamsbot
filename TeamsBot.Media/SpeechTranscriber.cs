using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace TeamsBot.Media;

/// <summary>
/// Per-meeting Azure Speech SDK session using PushAudioInputStream.
/// Receives raw 16kHz/16-bit/mono PCM from the RMP media session and
/// feeds it into Azure Speech for continuous recognition.
/// On each Recognized event, sends a JSON segment over the named pipe.
/// </summary>
public sealed class SpeechTranscriber : IAsyncDisposable
{
    private readonly string _speechKey;
    private readonly string _speechRegion;
    private readonly PipeServer _pipe;

    private readonly ConcurrentDictionary<string, RecognizerSession> _sessions = new();

    public SpeechTranscriber(string speechKey, string speechRegion, PipeServer pipe)
    {
        _speechKey    = speechKey;
        _speechRegion = speechRegion;
        _pipe         = pipe;
    }

    public void StartSession(string meetingId)
    {
        if (_sessions.ContainsKey(meetingId))
        {
            Console.WriteLine($"[SpeechTranscriber] Session already exists for {meetingId}");
            return;
        }

        var speechConfig = SpeechConfig.FromSubscription(_speechKey, _speechRegion);
        speechConfig.OutputFormat = OutputFormat.Detailed;
        speechConfig.SetProperty(
            PropertyId.SpeechServiceResponse_RequestWordLevelTimestamps, "true");

        // 16 kHz / 16-bit / mono — must match RMP SDK AudioFormat.Pcm16K
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
                var segment = new
                {
                    meetingId  = meetingId,
                    text       = e.Result.Text,
                    speakerId  = (string?)null,
                    language   = speechConfig.SpeechRecognitionLanguage ?? "en-US",
                    confidence = confidence,
                    timestamp  = DateTimeOffset.UtcNow.ToString("o")
                };

                var json = JsonSerializer.Serialize(segment);
                _ = _pipe.SendAsync(json);

                Console.WriteLine($"[{meetingId}] \u2713 {e.Result.Text}");
            }
        };

        recognizer.Canceled += (_, e) =>
        {
            Console.WriteLine(
                $"[{meetingId}] Speech canceled \u2014 Reason: {e.Reason}, Code: {e.ErrorCode}, Details: {e.ErrorDetails}");
        };

        recognizer.SessionStopped += (_, _) =>
            Console.WriteLine($"[{meetingId}] Speech session stopped");

        recognizer.StartContinuousRecognitionAsync().GetAwaiter().GetResult();

        _sessions[meetingId] = new RecognizerSession(recognizer, pushStream);
        Console.WriteLine($"[SpeechTranscriber] Started session for {meetingId}");
    }

    public void PushAudio(string meetingId, byte[] pcmBytes)
    {
        if (_sessions.TryGetValue(meetingId, out var session))
        {
            session.PushStream.Write(pcmBytes, pcmBytes.Length);
        }
    }

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
            Console.WriteLine($"[SpeechTranscriber] Stopped session for {meetingId}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _sessions)
            await StopSessionAsync(kvp.Key);
    }

    private static double TryGetConfidence(SpeechRecognitionResult result)
    {
        try
        {
            var json = result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult);
            if (!string.IsNullOrEmpty(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("NBest", out var nBest)
                    && nBest.GetArrayLength() > 0)
                {
                    return nBest[0].GetProperty("Confidence").GetDouble();
                }
            }
        }
        catch { }
        return 0.9;
    }

    private sealed class RecognizerSession
    {
        public SpeechRecognizer Recognizer { get; }
        public PushAudioInputStream PushStream { get; }
        public RecognizerSession(SpeechRecognizer recognizer, PushAudioInputStream pushStream)
        {
            Recognizer = recognizer;
            PushStream = pushStream;
        }
    }
}
