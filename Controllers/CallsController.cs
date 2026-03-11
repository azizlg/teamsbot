using System.Net.Http;
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

    private static readonly HttpClient _mediaHttp =
        new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

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

        _logger.LogInformation("Graph callback received ({Length} bytes): {Json}", json.Length, json[..Math.Min(json.Length, 2000)]);

        using var doc  = JsonDocument.Parse(json);
        var body       = doc.RootElement;

        var terminatedMeetings = new List<string>();

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
                _graph.StopTranscriptPolling(callId);
                terminatedMeetings.Add(meetingId);
                _logger.LogInformation("Call terminated: {CallId} / meeting {MeetingId}", callId, meetingId);
            }

            if (state == "established")
            {
                _transcriber.StartSession(meetingId);
                _graph.StartTranscriptPolling(callId, meetingId);
                _logger.LogInformation("Call established: {CallId} — transcript polling started", callId);
            }
        });

        // Save recordings and clean up terminated meetings (must be async, outside sync callback)
        foreach (var mid in terminatedMeetings)
        {
            try
            {
                await _store.SaveTranscriptAsync(mid);
                await _transcriber.StopSessionAsync(mid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save recordings for meeting {MeetingId}", mid);
            }

            // Tell TeamsBot.Media to tear down the audio socket for this meeting
            _ = _mediaHttp.DeleteAsync($"http://localhost:8446/session/{Uri.EscapeDataString(mid)}");

            _store.EndMeeting(mid);
        }

        // Graph expects a 200 OK acknowledgement
        return Ok(new { status = "ok" });
    }
}
