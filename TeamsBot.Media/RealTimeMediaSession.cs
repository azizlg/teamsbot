using System;
using System.Runtime.InteropServices;
using Microsoft.Skype.Bots.Media;

namespace TeamsBot.Media;

/// <summary>
/// Receives raw PCM audio from the RMP SDK and feeds it to the Azure Speech transcriber.
/// One instance per active call. Created by MediaPlatformService.CreateMediaSession().
/// </summary>
public sealed class RealTimeMediaSession : IDisposable
{
    private readonly string _meetingId;
    private readonly AudioSocket _audioSocket;
    private readonly SpeechTranscriber _transcriber;
    private long _frameCount;

    public RealTimeMediaSession(
        string meetingId,
        AudioSocket audioSocket,
        SpeechTranscriber transcriber)
    {
        _meetingId   = meetingId;
        _audioSocket = audioSocket;
        _transcriber = transcriber;

        _audioSocket.AudioMediaReceived += OnAudioMediaReceived;
    }

    public void Initialize()
    {
        _transcriber.StartSession(_meetingId);
        Console.WriteLine($"[{_meetingId}] Media session initialized — audio flowing");
    }

    public void Shutdown()
    {
        _ = _transcriber.StopSessionAsync(_meetingId);
        Console.WriteLine($"[{_meetingId}] Media session shutting down");
    }

    private void OnAudioMediaReceived(object sender, AudioMediaReceivedEventArgs e)
    {
        var buffer = e.Buffer;
        try
        {
            _frameCount++;

            // Log first frame and then every 500 frames (~10s at 50fps)
            if (_frameCount == 1)
                Console.WriteLine($"[{_meetingId}] *** First audio frame received — format: {buffer.AudioFormat}, length: {buffer.Length} bytes");
            else if (_frameCount % 500 == 0)
                Console.WriteLine($"[{_meetingId}] Audio frames: {_frameCount} received so far");

            // Assert PCM format at runtime — don't assume, verify.
            if (buffer.AudioFormat != AudioFormat.Pcm16K)
            {
                Console.WriteLine(
                    $"[{_meetingId}] Unexpected PCM format: {buffer.AudioFormat}. Expected Pcm16K. Dropping buffer.");
                return;
            }

            var pcmBytes = new byte[buffer.Length];
            Marshal.Copy(buffer.Data, pcmBytes, 0, (int)buffer.Length);

            _transcriber.PushAudio(_meetingId, pcmBytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_meetingId}] Error processing audio buffer: {ex.Message}");
        }
        finally
        {
            // CRITICAL: must always dispose — RMP SDK manages the buffer pool
            buffer.Dispose();
        }
    }

    public void Dispose()
    {
        _audioSocket.AudioMediaReceived -= OnAudioMediaReceived;
        _audioSocket.Dispose();
    }
}
