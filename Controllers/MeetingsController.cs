using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TeamsBot.Bot;
using TeamsBot.Meetings;
using TeamsBot.Transcription;

namespace TeamsBot.Controllers;

/// <summary>
/// REST endpoints for joining/leaving meetings and getting transcript data.
/// The bot can also be triggered programmatically via these endpoints.
/// </summary>
[ApiController]
[Route("meetings")]
public sealed class MeetingsController : ControllerBase
{
    private readonly GraphCallsService _graph;
    private readonly SpeechTranscriber _transcriber;
    private readonly MeetingStore _store;
    private readonly IConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MeetingsController> _logger;

    public MeetingsController(
        GraphCallsService graph,
        SpeechTranscriber transcriber,
        MeetingStore store,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        ILogger<MeetingsController> logger)
    {
        _graph       = graph;
        _transcriber = transcriber;
        _store       = store;
        _config      = config;
        _loggerFactory = loggerFactory;
        _logger      = logger;
    }

    /// <summary>
    /// POST /meetings/join — Join a Teams meeting by URL.
    /// Body: { "meetingUrl": "https://teams.microsoft.com/...", "meetingId": "optional" }
    /// </summary>
    [HttpPost("join")]
    public async Task<IActionResult> Join([FromBody] JoinMeetingRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.MeetingUrl))
            return BadRequest(new { error = "meetingUrl is required" });

        var meetingId = req.MeetingId?.Trim()
                        ?? Guid.NewGuid().ToString("N")[..12];

        if (_store.GetMeeting(meetingId) is not null)
            return Conflict(new { error = $"Meeting {meetingId} is already active" });

        try
        {
            var callbackUri = _config["TunnelUrl"]!.TrimEnd('/');

            // Try to get an appHostedMediaConfig blob from TeamsBot.Media (RMP SDK process).
            // Falls back to serviceHostedMediaConfig (empty blob) if the media process is unavailable.
            var blob = await FetchMediaBlobAsync(meetingId, ct);

            _store.StartMeeting(meetingId, req.MeetingUrl);

            var callId = await _graph.JoinMeetingAsync(req.MeetingUrl, meetingId, blob, callbackUri, req.TenantId, ct);

            _store.UpdateMeetingStatus(meetingId, "joining", callId);

            _logger.LogInformation("API join: meeting={Meeting}, callId={CallId}", meetingId, callId);

            return Ok(new
            {
                meeting_id    = meetingId,
                call_id       = callId,
                joined_via    = "graph",
                status        = "joining",
                dashboard_url = $"{callbackUri}/dashboard",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting {MeetingId}", meetingId);
            _store.UpdateMeetingStatus(meetingId, "error");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /meetings/{id}/leave — Leave an active meeting.
    /// </summary>
    [HttpPost("{meetingId}/leave")]
    public async Task<IActionResult> Leave(string meetingId, CancellationToken ct)
    {
        var meeting = _store.GetMeeting(meetingId);
        if (meeting is null)
            return NotFound(new { error = $"Meeting {meetingId} not found" });

        if (meeting.CallId is not null)
        {
            try { await _graph.LeaveAsync(meeting.CallId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error leaving call {CallId}", meeting.CallId); }
        }

        await _transcriber.StopSessionAsync(meetingId);
        _store.EndMeeting(meetingId);

        return Ok(new { meetingId, status = "ended" });
    }

    /// <summary>
    /// GET /meetings/{id}/transcript — Get the full transcript for a meeting.
    /// </summary>
    [HttpGet("{meetingId}/transcript")]
    public IActionResult GetTranscript(string meetingId)
    {
        var segments = _store.GetSegments(meetingId);
        return Ok(new
        {
            meeting_id     = meetingId,
            total_segments = segments.Count,
            segments       = segments.Select(s => new
            {
                text       = s.Text,
                speaker    = s.SpeakerId,
                language   = s.Language,
                timestamp  = s.Timestamp,
                confidence = s.Confidence,
                source     = "speech"
            })
        });
    }

    /// <summary>
    /// GET /meetings — List all active/recent meetings.
    /// </summary>
    [HttpGet]
    public IActionResult GetAll() => Ok(_store.GetAllMeetings());

    /// <summary>
    /// GET /meetings/{id}/status — Get meeting status.
    /// </summary>
    [HttpGet("{meetingId}/status")]
    public IActionResult GetStatus(string meetingId)
    {
        var meeting = _store.GetMeeting(meetingId);
        return meeting is null
            ? NotFound(new { error = $"Meeting {meetingId} not found" })
            : Ok(meeting);
    }

    /// <summary>
    /// GET /meetings/{id}/analysis — Analysis snapshot for the dashboard.
    /// </summary>
    [HttpGet("{meetingId}/analysis")]
    public IActionResult GetAnalysis(string meetingId)
    {
        var meeting = _store.GetMeeting(meetingId);
        if (meeting is null)
            return NotFound(new { error = $"Meeting {meetingId} not found" });

        return Ok(new
        {
            meeting_id = meetingId,
            analysis   = new
            {
                running_summary   = (string?)null,
                topics            = Array.Empty<object>(),
                action_items      = Array.Empty<object>(),
                decisions         = Array.Empty<object>(),
                questions         = Array.Empty<object>(),
                speaker_sentiment = new { },
                key_phrases       = Array.Empty<string>()
            }
        });
    }

    // ── media blob helper ──────────────────────────────────────────────────────

    private static readonly HttpClient _mediaHttp =
        new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Ask TeamsBot.Media (port 8446) to create an audio socket and return the
    /// appHostedMediaConfig blob. Returns empty string if the media process is
    /// unavailable, which causes GraphCallsService to fall back to serviceHostedMediaConfig.
    /// </summary>
    private async Task<string> FetchMediaBlobAsync(string meetingId, CancellationToken ct)
    {
        try
        {
            var uri  = $"http://localhost:8446/session/{Uri.EscapeDataString(meetingId)}";
            using var resp = await _mediaHttp.PostAsync(uri, null, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Media API returned {Status} for {MeetingId} — falling back to serviceHostedMediaConfig",
                    resp.StatusCode, meetingId);
                return string.Empty;
            }

            var json  = await resp.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var blob  = doc.RootElement.GetProperty("blob").GetString() ?? string.Empty;
            _logger.LogInformation("MediaApiServer returned blob ({Chars} chars) for {MeetingId}",
                blob.Length, meetingId);
            return blob;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("TeamsBot.Media unavailable ({Error}) — using serviceHostedMediaConfig", ex.Message);
            return string.Empty;
        }
    }
}
