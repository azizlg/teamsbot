using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Transport;
using TeamsBot.Transcription;

namespace TeamsBot.Media;

/// <summary>
/// Singleton wrapper around the RMP SDK's MediaPlatform.
/// Initialised once at startup. Creates per-call IMediaSession instances
/// that carry the appHostedMediaConfig blob for Graph.
/// </summary>
public sealed class MediaPlatformService : IAsyncDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<MediaPlatformService> _logger;
    private bool _initialized;

    public MediaPlatformService(IConfiguration config, ILogger<MediaPlatformService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Initialize the media platform. Must be called once before any calls are joined.
    /// Validates that ServiceFqdn matches config, loads the cert, and starts the platform.
    /// </summary>
    public Task InitializeAsync()
    {
        if (_initialized)
            return Task.CompletedTask;

        var fqdn     = _config["MediaPlatform:ServiceFqdn"]
                       ?? throw new InvalidOperationException("MediaPlatform:ServiceFqdn is required");
        var thumbprint = _config["MediaPlatform:CertThumbprint"]
                       ?? throw new InvalidOperationException("MediaPlatform:CertThumbprint is required");
        var publicPort  = _config.GetValue<int>("MediaPlatform:InstancePublicPort",  8445);
        var privatePort = _config.GetValue<int>("MediaPlatform:InstancePrivatePort", 8445);

        // ✅ ServiceFqdn must exactly match the public DNS name.
        //    Any mismatch causes ICE to fail silently — bot joins but no audio.
        _logger.LogInformation(
            "Initializing media platform: FQDN={Fqdn}, PublicPort={PublicPort}, Thumbprint={Thumb}",
            fqdn, publicPort, thumbprint[..Math.Min(thumbprint.Length, 8)] + "...");

        var cert = LoadCertificate(thumbprint);

        var settings = new MediaPlatformInstanceSettings
        {
            ServiceFqdn          = fqdn,
            CertificateThumbprint = thumbprint,
            InstancePublicPort   = publicPort,
            InstanceInternalPort = privatePort,
            ServiceCertificate   = cert,
        };

        MediaPlatform.Initialize(settings);
        _initialized = true;

        _logger.LogInformation("RMP media platform initialized on {Fqdn}:{Port}", fqdn, publicPort);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Create a new IMediaSession for a call and return it with its serialized blob.
    /// The blob goes into the Graph appHostedMediaConfig request body.
    /// </summary>
    public (IMediaSession Session, string Blob) CreateMediaSession(
        string meetingId,
        SpeechTranscriber transcriber,
        ILoggerFactory loggerFactory)
    {
        if (!_initialized)
            throw new InvalidOperationException("MediaPlatform not initialized — call InitializeAsync first");

        var audioSettings = new AudioSocketSettings
        {
            StreamDirections          = StreamDirection.Receiveonly,
            SupportedAudioFormat      = AudioFormat.Pcm16K,   // 16 kHz mono PCM
            ReceiveUnmixedMeetingAudio = true,
        };

        var audioSocket = new AudioSocket(audioSettings);
        var mediaSetting = new MediaSessionSettings { AudioSocketSettings = [audioSettings] };
        var session = MediaPlatform.CreateMediaSession(mediaSetting);

        // Wire up the audio socket inside the session to our transcriber
        // (RealTimeMediaSession wraps the audio socket event handling)
        var rtms = new RealTimeMediaSession(
            meetingId: meetingId,
            audioSocket: audioSocket,
            transcriber: transcriber,
            logger: loggerFactory.CreateLogger<RealTimeMediaSession>());

        // Serialize the media configuration to base64 (required by Graph)
        var blob = Convert.ToBase64String(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(session.MediaConfiguration));

        _logger.LogDebug("Created media session for {MeetingId}, blob length={Len}", meetingId, blob.Length);

        return (rtms, blob);
    }

    public ValueTask DisposeAsync()
    {
        if (_initialized)
        {
            try { MediaPlatform.Shutdown(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error shutting down media platform"); }
        }
        return ValueTask.CompletedTask;
    }

    // ── certificate loader ────────────────────────────────────────────────────

    private static X509Certificate2 LoadCertificate(string thumbprint)
    {
        // Search both LocalMachine and CurrentUser stores
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var certs = store.Certificates
                .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (certs.Count > 0)
                return certs[0];
        }

        throw new InvalidOperationException(
            $"Certificate with thumbprint '{thumbprint}' not found in LocalMachine\\My or CurrentUser\\My. " +
            $"Run setup-cert.ps1 to import the Caddy TLS certificate, or provide a self-signed cert.");
    }
}
