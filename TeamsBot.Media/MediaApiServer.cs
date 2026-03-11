using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TeamsBot.Media;

/// <summary>
/// Minimal HTTP server (HttpListener on http://localhost:8446/) that lets
/// TeamsBot.Web request media session blobs before joining a meeting and
/// tear them down when the call ends.
///
/// Endpoints:
///   POST   /session/{meetingId} → CreateMediaSession + InitializeSession → { "blob": "..." }
///   DELETE /session/{meetingId} → ShutdownSession → { "ok": true }
/// </summary>
internal sealed class MediaApiServer : IDisposable
{
    private const string Prefix = "http://localhost:8446/";

    private readonly MediaPlatformService _platform;
    private readonly HttpListener _listener;

    public MediaApiServer(MediaPlatformService platform)
    {
        _platform = platform;
        _listener = new HttpListener();
        _listener.Prefixes.Add(Prefix);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        Console.WriteLine($"[MediaApiServer] Listening on {Prefix}");

        ct.Register(() =>
        {
            try { _listener.Stop(); } catch { }
        });

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break; // listener was stopped
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req  = ctx.Request;
        var resp = ctx.Response;
        resp.ContentType = "application/json";

        try
        {
            // Path: /session/{meetingId}
            var path  = req.Url?.AbsolutePath?.Trim('/') ?? "";
            var parts = path.Split('/');

            if (parts.Length < 2 ||
                !parts[0].Equals("session", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 404;
                await WriteAsync(resp, "{\"error\":\"not found\"}");
                return;
            }

            var meetingId = Uri.UnescapeDataString(parts[1]);
            if (string.IsNullOrEmpty(meetingId))
            {
                resp.StatusCode = 400;
                await WriteAsync(resp, "{\"error\":\"meetingId required\"}");
                return;
            }

            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[MediaApiServer] Creating media session for {meetingId}");
                var (_, blob) = _platform.CreateMediaSession(meetingId);
                // InitializeSession (speech recognizer) is started lazily when the first
                // audio frame arrives — see RealTimeMediaSession.OnAudioMediaReceived.
                Console.WriteLine($"[MediaApiServer] Session created, blob length={blob?.Length ?? 0} chars");
                var blobJson  = JsonSerializer.Serialize(blob);  // quoted + escaped
                resp.StatusCode = 200;
                await WriteAsync(resp, $"{{\"blob\":{blobJson}}}");
            }
            else if (req.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                _platform.ShutdownSession(meetingId);
                resp.StatusCode = 200;
                await WriteAsync(resp, "{\"ok\":true}");
            }
            else
            {
                resp.StatusCode = 405;
                await WriteAsync(resp, "{\"error\":\"method not allowed\"}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MediaApiServer] Error {req.HttpMethod} {req.Url}: {ex.Message}");
            Console.WriteLine($"[MediaApiServer] Stack: {ex}");
            resp.StatusCode = 500;
            try
            {
                var msg = JsonSerializer.Serialize(ex.Message);
                await WriteAsync(resp, $"{{\"error\":{msg}}}");
            }
            catch { }
        }
        finally
        {
            resp.Close();
        }
    }

    private static async Task WriteAsync(HttpListenerResponse resp, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}
