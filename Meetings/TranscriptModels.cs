using System;
using System.Text.Json.Serialization;

namespace TeamsBot.Meetings;

/// <summary>A single recognized speech segment from a meeting.</summary>
public sealed record TranscriptSegment(
    string MeetingId,
    string Text,
    string Language,
    double Timestamp,
    double Confidence,
    string? SpeakerId
);

/// <summary>Snapshot of an active meeting for status responses.</summary>
public sealed record MeetingInfo(
    string MeetingId,
    string JoinUrl,
    string Status,
    string? CallId,
    DateTime StartedAt,
    int SegmentCount
);

/// <summary>Request body for POST /meetings/join.</summary>
public sealed record JoinMeetingRequest(
    [property: JsonPropertyName("meeting_url")] string  MeetingUrl,
    [property: JsonPropertyName("meeting_id")]  string? MeetingId,
    [property: JsonPropertyName("tenant_id")]   string? TenantId);
