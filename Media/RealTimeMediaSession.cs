using System.Runtime.InteropServices;
using Microsoft.Graph.Communications.Calls.Media;
using TeamsBot.Transcription;

namespace TeamsBot.Media;

/// <summary>
/// Receives raw PCM audio from the RMP SDK and feeds it to the Azure Speech transcriber.
/// One instance per active call. Created by MediaPlatformService.CreateMediaSession().
/// </summary>
public sealed class RealTimeMediaSession : IMediaSession
{
    private readonly string _meetingId;
    private readonly AudioSocket _audioSocket;
    private readonly SpeechTranscriber _transcriber;
    private readonly ILogger<RealTimeMediaSession> _logger;

    public string Id => _meetingId;

    // The RMP SDK queries this list to negotiate media capabilities
    IReadOnlyCollection<IMediaSocket> IMediaSession.Sockets => [_audioSocket];

    public RealTimeMediaSession(
        string meetingId,
        AudioSocket audioSocket,
        SpeechTranscriber transcriber,
        ILogger<RealTimeMediaSession> logger)
    {
        _meetingId   = meetingId;
        _audioSocket = audioSocket;
        _transcriber = transcriber;
        _logger      = logger;

        _audioSocket.AudioMediaReceived    += OnAudioMediaReceived;
        _audioSocket.DominantSpeakerChanged += OnDominantSpeakerChanged;
        _audioSocket.AudioSendStatusChanged += OnAudioSendStatusChanged;
    }

    // ── audio frame handler ───────────────────────────────────────────────────

    private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
    {
        var buffer = e.Buffer;
        try
        {
            // ✅ Assert PCM format at runtime — don't assume, verify.
            // RMP SDK should deliver Pcm16K (16kHz / 16-bit / mono) per AudioSocketSettings,
            // but we guard against unexpected format changes causing silent corruption.
            if (buffer.AudioFormat != AudioFormat.Pcm16K)
            {
                _logger.LogError(
                    "[{MeetingId}] Unexpected PCM format received: {Format}. " +
                    "Expected: Pcm16K. Dropping buffer to avoid feeding corrupt audio to Speech SDK.",
                    _meetingId, buffer.AudioFormat);
                return;
            }

            // Copy PCM bytes out of the unmanaged IntPtr buffer
            var pcmBytes = new byte[buffer.Length];
            Marshal.Copy(buffer.Data, pcmBytes, 0, (int)buffer.Length);

            _transcriber.PushAudio(_meetingId, pcmBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{MeetingId}] Error processing audio buffer", _meetingId);
        }
        finally
        {
            // CRITICAL: must always dispose — RMP SDK manages the buffer pool
            buffer.Dispose();
        }
    }

    private void OnDominantSpeakerChanged(object? sender, DominantSpeakerChangedEventArgs e)
    {
        _logger.LogDebug(
            "[{MeetingId}] Dominant speaker → {Speaker}", _meetingId, e.CurrentDominantSpeaker);
    }

    private void OnAudioSendStatusChanged(object? sender, AudioSendStatusChangedEventArgs e)
    {
        // Receive-only socket, so send status changes are unexpected but logged
        _logger.LogDebug(
            "[{MeetingId}] AudioSendStatus → {Status}", _meetingId, e.MediaSendStatus);
    }

    // ── IMediaSession lifecycle ───────────────────────────────────────────────

    void IMediaSession.Initialize() =>
        _logger.LogInformation("[{MeetingId}] Media session initialized — audio flowing", _meetingId);

    void IMediaSession.Shutdown() =>
        _logger.LogInformation("[{MeetingId}] Media session shutting down", _meetingId);

    public void Dispose()
    {
        _audioSocket.AudioMediaReceived     -= OnAudioMediaReceived;
        _audioSocket.DominantSpeakerChanged -= OnDominantSpeakerChanged;
        _audioSocket.AudioSendStatusChanged -= OnAudioSendStatusChanged;
        _audioSocket.Dispose();
    }
}
