using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TeamsBot.Bot;
using TeamsBot.Meetings;
using TeamsBot.Transcription;

namespace TeamsBot.Controllers;

/// <summary>
/// Handles Graph Communications API callback notifications.
/// Path: POST /api/calls/callback
///
/// Graph sends call state changes here (establishing → established → terminated).
/// The RMP SDK must also process these notifications via its own registration.
/// </summary>
[ApiController]
[Route("api/calls")]
public sealed class CallsController : ControllerBase
{
    private readonly GraphCallsService _graph;
    private readonly MeetingStore _store;
    private readonly SpeechTranscriber _transcriber;
    private readonly ILogger<CallsController> _logger;

    public CallsController(
        GraphCallsService graph,
        MeetingStore store,
        SpeechTranscriber transcriber,
        ILogger<CallsController> logger)
    {
        _graph       = graph;
        _store       = store;
        _transcriber = transcriber;
        _logger      = logger;
    }

    [HttpPost("callback")]
    public async Task<IActionResult> Callback()
    {
        string json;
        using (var sr = new System.IO.StreamReader(Request.Body))
            json = await sr.ReadToEndAsync();

        _logger.LogDebug("Graph callback received: {Json}", json[..Math.Min(json.Length, 300)]);

        using var doc  = JsonDocument.Parse(json);
        var body       = doc.RootElement;

        _graph.HandleCallback(body, (callId, state) =>
        {
            var meetingId = _graph.GetMeetingId(callId);
            if (meetingId is null)
            {
                _logger.LogWarning("Callback for unknown callId={CallId}", callId);
                return;
            }

            _store.UpdateMeetingStatus(meetingId, state switch
            {
                "established"  => "active",
                "terminated"   => "ended",
                "establishing" => "joining",
                _              => state,
            }, callId);

            if (state == "terminated")
            {
                _ = _transcriber.StopSessionAsync(meetingId);
                _store.EndMeeting(meetingId);
                _logger.LogInformation("Call terminated: {CallId} / meeting {MeetingId}", callId, meetingId);
            }

            if (state == "established")
                _logger.LogInformation("Call established: {CallId} — audio flowing to Speech SDK", callId);
        });

        // Graph expects a 200 OK acknowledgement
        return Ok(new { status = "ok" });
    }
}
