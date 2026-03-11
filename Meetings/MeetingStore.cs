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

    /// <summary>Save the transcript as JSON to the recordings directory.</summary>
    public async Task SaveTranscriptAsync(string meetingId)
    {
        var segments = GetSegments(meetingId);
        if (segments.Count == 0)
        {
            _logger.LogInformation("[{MeetingId}] No transcript segments to save", meetingId);
            return;
        }

        var dir = Path.Combine(AppContext.BaseDirectory, "recordings", meetingId);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "transcript.json");

        var data = segments.Select(s => new
        {
            timestamp  = s.Timestamp,
            speaker_id = s.SpeakerId,
            text       = s.Text,
            language   = s.Language,
            confidence = s.Confidence
        }).ToList();

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);

        _logger.LogInformation("[{MeetingId}] Transcript saved: {Path} ({Count} segments)", meetingId, filePath, segments.Count);
    }

    /// <summary>Save transcripts for all meetings that have segments (crash-safety flush).</summary>
    public async Task SaveAllActiveTranscriptsAsync()
    {
        foreach (var meeting in GetAllMeetings())
        {
            try { await SaveTranscriptAsync(meeting.MeetingId); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to save transcript for {MeetingId}", meeting.MeetingId); }
        }
    }

    // -----------------------------------------------------------------------
    // WebSocket management
    // -----------------------------------------------------------------------

    public async Task HandleWebSocketAsync(string meetingId, WebSocket ws, CancellationToken ct)
    {
        var bag = _webSockets.GetOrAdd(meetingId, _ => []);
        bag.Add(ws);
        _logger.LogDebug("WebSocket connected for meeting {MeetingId}", meetingId);

        // Send a single catch-up message containing all existing segments
        var existing = GetSegments(meetingId);
        var catchupJson = JsonSerializer.Serialize(new
        {
            type = "catchup",
            segments = existing.Select(s => new
            {
                text       = s.Text,
                speaker    = s.SpeakerId,
                language   = s.Language,
                timestamp  = s.Timestamp,
                confidence = s.Confidence,
                source     = "speech"
            }).ToList(),
            analysis = new { }
        });
        var catchupBytes = Encoding.UTF8.GetBytes(catchupJson);
        if (ws.State == WebSocketState.Open)
            await ws.SendAsync(catchupBytes, WebSocketMessageType.Text, endOfMessage: true, ct);

        // Keep connection alive until client disconnects.
        // Send a heartbeat every 25 s so nginx proxy_read_timeout doesn't drop idle connections.
        var buffer = new byte[1024];
        var heartbeatBytes = Encoding.UTF8.GetBytes("{\"type\":\"heartbeat\"}");
        try
        {
            var recvTask = ws.ReceiveAsync(buffer, ct);
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(recvTask, Task.Delay(25_000, ct));
                if (completed == recvTask)
                {
                    var result = await recvTask;
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    recvTask = ws.ReceiveAsync(buffer, ct);
                }
                else if (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    await ws.SendAsync(heartbeatBytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely) { }
        finally
        {
            _logger.LogDebug("WebSocket disconnected for meeting {MeetingId}", meetingId);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None); } catch { }
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
            type = "transcript_segment",
            segment = new
            {
                text       = seg.Text,
                speaker    = seg.SpeakerId,
                language   = seg.Language,
                timestamp  = seg.Timestamp,
                confidence = seg.Confidence,
                source     = "speech"
            }
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}
