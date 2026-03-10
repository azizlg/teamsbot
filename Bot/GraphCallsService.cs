using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TeamsBot.Meetings;

namespace TeamsBot.Bot;

/// <summary>
/// Calls the Microsoft Graph Communications REST API to join and leave Teams meetings.
///
/// Token refresh: Graph tokens expire after ~3599 seconds. This service caches the
/// token and refreshes it 5 minutes before expiry so long meetings never break.
/// </summary>
public sealed class GraphCallsService
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string TokenEndpoint =
        "https://login.microsoftonline.com/{0}/oauth2/v2.0/token";

    private readonly IConfiguration _config;
    private readonly MeetingStore _store;
    private readonly ILogger<GraphCallsService> _logger;
    private readonly HttpClient _http;

    // ── per-tenant token cache ─────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expiry)> _tokenCache = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // ── active calls ─────────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, string> _callToMeeting = new(); // callId → meetingId

    public GraphCallsService(
        IConfiguration config,
        MeetingStore store,
        ILogger<GraphCallsService> logger,
        IHttpClientFactory httpFactory)
    {
        _config = config;
        _store  = store;
        _logger = logger;
        _http   = httpFactory.CreateClient("Graph");
    }

    // ── public call ID lookup ─────────────────────────────────────────────────
    public string? GetMeetingId(string callId) =>
        _callToMeeting.TryGetValue(callId, out var m) ? m : null;

    // ── join ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Join a Teams meeting via POST /communications/calls.
    /// Returns the Graph call ID on success.
    /// </summary>
    public async Task<string> JoinMeetingAsync(
        string joinUrl,
        string meetingId,
        string mediaBlob,          // base64 blob from MediaPlatformService
        string callbackUri,
        string? organizerTenant = null,
        CancellationToken ct = default)
    {
        // Priority: explicit organizer tenant → extracted from URL → home tenant fallback
        var tenantId = organizerTenant
                       ?? ExtractTenantFromUrl(joinUrl)
                       ?? _config["MicrosoftAppTenantId"]!;

        // Token must be acquired for the same tenant used in the call body
        var token = await GetAccessTokenAsync(tenantId, ct);
        var meetingInfo = BuildMeetingInfo(joinUrl);

        var body = new JsonObject
        {
            ["@odata.type"]       = "#microsoft.graph.call",
            ["callbackUri"]       = $"{callbackUri}/api/calls/callback",
            ["requestedModalities"] = new JsonArray("audio"),
            // serviceHostedMediaConfig: Graph's cloud media server handles the audio stream.
            // appHostedMediaConfig (RMP SDK) only works on .NET Framework 4.x — see Media/ folder.
            ["mediaConfig"] = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.serviceHostedMediaConfig",
            },
            ["meetingInfo"] = meetingInfo,
            ["tenantId"]    = tenantId,
            ["source"] = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.participantInfo",
                ["identity"] = new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.identitySet",
                    ["application"] = new JsonObject
                    {
                        ["@odata.type"] = "#microsoft.graph.identity",
                        ["id"]          = _config["MicrosoftAppId"],
                        ["displayName"] = "Meeting Agent",
                    }
                }
            }
        };

        _logger.LogInformation("Joining meeting {MeetingId} (tenant={Tenant})", meetingId, tenantId[..8] + "...");

        var req = new HttpRequestMessage(HttpMethod.Post, $"{GraphBase}/communications/calls")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Graph join failed: {Status} — {Error}", resp.StatusCode, err);
            throw new InvalidOperationException($"Graph join failed: {resp.StatusCode} — {err[..Math.Min(err.Length, 400)]}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var callId = JsonDocument.Parse(json).RootElement.GetProperty("id").GetString()!;

        _callToMeeting[callId] = meetingId;
        _logger.LogInformation("Graph call created: callId={CallId}, meeting={MeetingId}", callId, meetingId);

        return callId;
    }

    // ── leave ─────────────────────────────────────────────────────────────────

    public async Task LeaveAsync(string callId, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct: ct);
        var req = new HttpRequestMessage(HttpMethod.Delete, $"{GraphBase}/communications/calls/{callId}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };
        var resp = await _http.SendAsync(req, ct);
        _callToMeeting.TryRemove(callId, out _);

        _logger.LogInformation("Left call {CallId}: HTTP {Status}", callId, resp.StatusCode);
    }

    // ── callback handling ─────────────────────────────────────────────────────

    /// <summary>
    /// Process an incoming Graph notification payload.
    /// Updates meeting state when the call becomes established or terminates.
    /// </summary>
    public void HandleCallback(JsonElement body, Action<string, string> onStateChange)
    {
        // Graph sends either { value: [...] } or a single notification
        var notifications = body.TryGetProperty("value", out var arr)
            ? arr.EnumerateArray()
            : new[] { body }.AsEnumerable().Select(x => x);

        foreach (var notif in notifications)
        {
            var resourceUrl = notif.TryGetProperty("resourceUrl", out var ru) ? ru.GetString() ?? "" : "";
            if (!resourceUrl.Contains("/communications/calls/"))
                continue;

            var resource = notif.TryGetProperty("resourceData", out var rd) ? rd : default;
            if (resource.ValueKind == JsonValueKind.Undefined)
                continue;

            var callId = resource.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
            var state  = resource.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(callId)) continue;

            _logger.LogInformation("Graph callback: callId={CallId}, state={State}", callId, state);
            onStateChange(callId, state);
        }
    }

    // ── token — with expiry-aware refresh ─────────────────────────────────────

    /// <summary>
    /// Get a valid Graph access token.
    /// Token is cached and refreshed 5 minutes before expiry to handle meetings
    /// that last longer than the standard ~1-hour token lifetime.
    /// </summary>
    private async Task<string> GetAccessTokenAsync(string? tenantId = null, CancellationToken ct = default)
    {
        tenantId ??= _config["MicrosoftAppTenantId"]!;

        // Fast path — cached token still valid with 5-minute buffer
        if (_tokenCache.TryGetValue(tenantId, out var cached) &&
            DateTimeOffset.UtcNow < cached.Expiry.AddMinutes(-5))
            return cached.Token;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Double-check inside lock
            if (_tokenCache.TryGetValue(tenantId, out cached) &&
                DateTimeOffset.UtcNow < cached.Expiry.AddMinutes(-5))
                return cached.Token;

            var url = string.Format(TokenEndpoint, tenantId);

            var form = new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = _config["MicrosoftAppId"]!,
                ["client_secret"] = _config["MicrosoftAppPassword"]!,
                ["scope"]         = "https://graph.microsoft.com/.default",
            };

            var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
            resp.EnsureSuccessStatusCode();

            var json    = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var token     = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            var expiry    = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            _tokenCache[tenantId] = (token, expiry);

            _logger.LogInformation(
                "Graph token acquired for tenant {Tenant}. Expires at {Expiry} UTC (+{Sec}s).",
                tenantId[..8] + "...", expiry.UtcDateTime, expiresIn);

            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ── URL helpers ───────────────────────────────────────────────────────────

    private static readonly Regex _newMeetRe =
        new(@"teams\.microsoft\.com/meet/([^/?#&]+)", RegexOptions.IgnoreCase);

    private static JsonObject BuildMeetingInfo(string joinUrl)
    {
        // New short-link: teams.microsoft.com/meet/<joinId>?p=<passcode>
        var m = _newMeetRe.Match(joinUrl);
        if (m.Success)
        {
            var node = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.joinMeetingIdMeetingInfo",
                ["joinMeetingId"] = m.Groups[1].Value,
            };
            var qs = System.Web.HttpUtility.ParseQueryString(new Uri(joinUrl).Query);
            if (qs["p"] is { } passcode)
                node["passcode"] = passcode;
            return node;
        }

        // Old-format /l/meetup-join/ — use tokenMeetingInfo
        return new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.tokenMeetingInfo",
            ["token"]       = joinUrl,
        };
    }

    private static string? ExtractTenantFromUrl(string joinUrl)
    {
        try
        {
            var qs  = System.Web.HttpUtility.ParseQueryString(new Uri(joinUrl).Query);
            var ctx = qs["context"];
            if (ctx is null) return null;
            var doc = JsonDocument.Parse(Uri.UnescapeDataString(ctx));
            return doc.RootElement.TryGetProperty("Tid", out var tid) ? tid.GetString() : null;
        }
        catch { return null; }
    }
}
