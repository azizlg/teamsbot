using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Skype.Bots.Media;

namespace TeamsBot.Media;

/// <summary>
/// Singleton wrapper around the RMP SDK's MediaPlatform.
/// Initialised once at startup. Creates per-call audio sockets
/// and generates the appHostedMediaConfig blob for Graph.
/// </summary>
public sealed class MediaPlatformService : IDisposable
{
    private readonly string _appId;
    private readonly string _fqdn;
    private readonly string _certThumbprint;
    private readonly int _publicPort;
    private readonly int _privatePort;
    private readonly SpeechTranscriber _transcriber;
    private bool _initialized;

    private readonly ConcurrentDictionary<string, RealTimeMediaSession> _sessions =
        new ConcurrentDictionary<string, RealTimeMediaSession>();

    public MediaPlatformService(
        string appId,
        string fqdn,
        string certThumbprint,
        int publicPort,
        int privatePort,
        SpeechTranscriber transcriber)
    {
        _appId          = appId;
        _fqdn           = fqdn;
        _certThumbprint = certThumbprint;
        _publicPort     = publicPort;
        _privatePort    = privatePort;
        _transcriber    = transcriber;
    }

    public void Initialize()
    {
        if (_initialized) return;

        Console.WriteLine($"[MediaPlatform] Initializing: FQDN={_fqdn}, Port={_publicPort}");

        var instanceSettings = new MediaPlatformInstanceSettings
        {
            ServiceFqdn           = _fqdn,
            CertificateThumbprint = _certThumbprint,
            InstancePublicPort    = _publicPort,
            InstanceInternalPort  = _privatePort,
            InstancePublicIPAddress = Dns.GetHostAddresses(_fqdn)[0],
        };

        var settings = new MediaPlatformSettings
        {
            ApplicationId                 = _appId,
            MediaPlatformInstanceSettings = instanceSettings,
            MediaPlatformLogger           = null,
        };

        MediaPlatform.Initialize(settings);
        _initialized = true;

        Console.WriteLine($"[MediaPlatform] Initialized on {_fqdn}:{_publicPort}");
    }

    /// <summary>
    /// Create a new audio socket for a call and return the serialized media configuration
    /// blob that goes into Graph's appHostedMediaConfig.
    /// </summary>
    public (RealTimeMediaSession Session, string Blob) CreateMediaSession(string meetingId)
    {
        if (!_initialized)
            throw new InvalidOperationException("MediaPlatform not initialized");

        var audioSettings = new AudioSocketSettings
        {
            StreamDirections           = StreamDirection.Recvonly,
            SupportedAudioFormat       = AudioFormat.Pcm16K,
            ReceiveUnmixedMeetingAudio = false,
            CallId                     = Guid.NewGuid().ToString(),
        };

        var audioSocket = new AudioSocket(audioSettings);
        var rtms = new RealTimeMediaSession(meetingId, audioSocket, _transcriber);

        // Generate the media config blob (base64-encoded JSON) for Graph appHostedMediaConfig
        var mediaConfig = MediaPlatform.CreateMediaConfiguration(audioSocket);
        var blob = mediaConfig.ToString();

        _sessions[meetingId] = rtms;

        Console.WriteLine($"[MediaPlatform] Created media session for {meetingId}");
        return (rtms, blob);
    }

    /// <summary>Initialize a previously created session (called when call is established).</summary>
    public void InitializeSession(string meetingId)
    {
        RealTimeMediaSession session;
        if (_sessions.TryGetValue(meetingId, out session))
            session.Initialize();
    }

    /// <summary>Shut down and remove a session (called when call terminates).</summary>
    public void ShutdownSession(string meetingId)
    {
        RealTimeMediaSession session;
        if (_sessions.TryRemove(meetingId, out session))
        {
            session.Shutdown();
            session.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _sessions)
        {
            kvp.Value.Shutdown();
            kvp.Value.Dispose();
        }
        _sessions.Clear();
    }
}
