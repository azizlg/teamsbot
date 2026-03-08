using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TeamsBot.Meetings;

/// <summary>
/// Thread-safe in-memory store for active meetings and transcript segments.
/// Also manages WebSocket connections for live dashboard streaming.
/// </summary>
public sealed class MeetingStore
{
    private readonly ILogger<MeetingStore> _logger;

    // meetingId → list of transcript segments (append-only)
    private readonly ConcurrentDictionary<string, List<TranscriptSegment>> _segments = new();

    // meetingId → meeting metadata
    private readonly ConcurrentDictionary<string, MeetingInfo> _meetings = new();

    // meetingId → set of active WebSocket connections
    private readonly ConcurrentDictionary<string, ConcurrentBag<WebSocket>> _webSockets = new();

    public MeetingStore(ILogger<MeetingStore> logger)
    {
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Meeting lifecycle
    // -----------------------------------------------------------------------

    public MeetingInfo StartMeeting(string meetingId, string joinUrl, string? callId = null)
    {
        var info = new MeetingInfo(meetingId, joinUrl, "joining", callId, DateTime.UtcNow, 0);
        _meetings[meetingId] = info;
        _segments.TryAdd(meetingId, []);
        _webSockets.TryAdd(meetingId, []);
        _logger.LogInformation("Meeting started: {MeetingId}", meetingId);
        return info;
    }

    public void UpdateMeetingStatus(string meetingId, string status, string? callId = null)
    {
        if (_meetings.TryGetValue(meetingId, out var existing))
        {
            _meetings[meetingId] = existing with
            {
                Status = status,
                CallId = callId ?? existing.CallId,
                SegmentCount = GetSegments(meetingId).Count,
            };
        }
    }

    public void EndMeeting(string meetingId)
    {
        UpdateMeetingStatus(meetingId, "ended");
        // Clean up WebSocket bag (connections should already be closed)
        _webSockets.TryRemove(meetingId, out _);
        _logger.LogInformation("Meeting ended: {MeetingId}", meetingId);
    }

    public MeetingInfo? GetMeeting(string meetingId) =>
        _meetings.TryGetValue(meetingId, out var m) ? m : null;

    public IReadOnlyList<MeetingInfo> GetAllMeetings() =>
        _meetings.Values.ToList().AsReadOnly();

    // -----------------------------------------------------------------------
    // Transcript segments
    // -----------------------------------------------------------------------

    public void AddSegment(TranscriptSegment segment)
    {
        var list = _segments.GetOrAdd(segment.MeetingId, _ => []);
        lock (list)
        {
            list.Add(segment);
        }

        // Update segment count in meeting info
        if (_meetings.TryGetValue(segment.MeetingId, out var info))
            _meetings[segment.MeetingId] = info with { SegmentCount = list.Count };

        _logger.LogDebug("[{MeetingId}] Transcript segment added: {Text}", segment.MeetingId, segment.Text);

        // Broadcast to all connected WebSocket clients for this meeting
        _ = BroadcastSegmentAsync(segment);
    }

    public IReadOnlyList<TranscriptSegment> GetSegments(string meetingId)
    {
        if (!_segments.TryGetValue(meetingId, out var list))
            return [];
        lock (list)
        {
            return list.ToList().AsReadOnly();
        }
    }

    // -----------------------------------------------------------------------
    // WebSocket management
    // -----------------------------------------------------------------------

    public async Task HandleWebSocketAsync(string meetingId, WebSocket ws, CancellationToken ct)
    {
        var bag = _webSockets.GetOrAdd(meetingId, _ => []);
        bag.Add(ws);
        _logger.LogInformation("WebSocket connected for meeting {MeetingId}", meetingId);

        // Send existing segments on connect (catch-up)
        foreach (var seg in GetSegments(meetingId))
        {
            await SendSegmentAsync(ws, seg, ct);
        }

        // Keep connection alive until client disconnects
        var buffer = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely) { }
        finally
        {
            _logger.LogInformation("WebSocket disconnected for meeting {MeetingId}", meetingId);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
        }
    }

    private async Task BroadcastSegmentAsync(TranscriptSegment segment)
    {
        if (!_webSockets.TryGetValue(segment.MeetingId, out var bag))
            return;

        var dead = new List<WebSocket>();
        foreach (var ws in bag)
        {
            if (ws.State != WebSocketState.Open)
            {
                dead.Add(ws);
                continue;
            }
            try
            {
                await SendSegmentAsync(ws, segment, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send to WebSocket for meeting {MeetingId}", segment.MeetingId);
                dead.Add(ws);
            }
        }

        // Remove dead connections (ConcurrentBag doesn't support removal, recreate)
        if (dead.Count > 0)
        {
            var fresh = new ConcurrentBag<WebSocket>(bag.Except(dead));
            _webSockets[segment.MeetingId] = fresh;
        }
    }

    private static async Task SendSegmentAsync(WebSocket ws, TranscriptSegment seg, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            type       = "transcript",
            meetingId  = seg.MeetingId,
            text       = seg.Text,
            language   = seg.Language,
            timestamp  = seg.Timestamp,
            confidence = seg.Confidence,
            speakerId  = seg.SpeakerId,
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}
