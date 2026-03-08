using Microsoft.AspNetCore.Mvc;
using TeamsBot.Bot;
using TeamsBot.Media;
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
    private readonly MediaPlatformService _media;
    private readonly SpeechTranscriber _transcriber;
    private readonly MeetingStore _store;
    private readonly IConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MeetingsController> _logger;

    public MeetingsController(
        GraphCallsService graph,
        MediaPlatformService media,
        SpeechTranscriber transcriber,
        MeetingStore store,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        ILogger<MeetingsController> logger)
    {
        _graph       = graph;
        _media       = media;
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

            var (_, blob) = _media.CreateMediaSession(meetingId, _transcriber, _loggerFactory);

            _store.StartMeeting(meetingId, req.MeetingUrl);

            var callId = await _graph.JoinMeetingAsync(req.MeetingUrl, meetingId, blob, callbackUri, ct);

            _store.UpdateMeetingStatus(meetingId, "joining", callId);

            _logger.LogInformation("API join: meeting={Meeting}, callId={CallId}", meetingId, callId);

            return Ok(new
            {
                meetingId,
                callId,
                status      = "joining",
                dashboardUrl = $"{callbackUri}/dashboard",
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
            meetingId,
            totalSegments = segments.Count,
            segments      = segments,
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
}
