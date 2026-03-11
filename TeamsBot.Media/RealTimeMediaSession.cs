using System;
using System.Runtime.InteropServices;
using System.Threading;
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
    private int _speechStarted; // 0 = not started, 1 = started (interlocked flag)

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
        // Speech session is now started lazily on first audio frame to avoid
        // loading Speech SDK native DLLs before the call is established.
        Console.WriteLine($"[{_meetingId}] Media session ready — waiting for audio");
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
            {
                Console.WriteLine($"[{_meetingId}] *** First audio frame received — format: {buffer.AudioFormat}, length: {buffer.Length} bytes");

                // Lazy-init speech recognizer on first audio frame
                if (Interlocked.CompareExchange(ref _speechStarted, 1, 0) == 0)
                {
                    try
                    {
                        _transcriber.StartSession(_meetingId);
                        Console.WriteLine($"[{_meetingId}] Speech recognizer started");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{_meetingId}] SPEECH INIT ERROR: {ex}");
                    }
                }
            }
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
