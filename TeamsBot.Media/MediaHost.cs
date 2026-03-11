using System;
using System.Threading;
using System.Threading.Tasks;

namespace TeamsBot.Media;

/// <summary>
/// Console entry point for the TeamsBot.Media host process.
/// Initializes the RMP SDK media platform, starts the named pipe server,
/// and keeps the process alive until Ctrl+C or service stop.
///
/// Environment variables:
///   MEDIA_SERVICE_FQDN       — public DNS (e.g. teams-bot.westeurope.cloudapp.azure.com)
///   MEDIA_CERT_THUMBPRINT    — cert thumbprint in LocalMachine\My
///   MEDIA_PUBLIC_PORT         — RMP public port (default 8445)
///   MEDIA_PRIVATE_PORT        — RMP internal port (default 8445)
///   AZURE_SPEECH_KEY          — Azure Cognitive Services Speech key
///   AZURE_SPEECH_REGION       — Azure Speech region (e.g. eastus)
///   MICROSOFT_APP_ID          — Bot app registration ID
/// </summary>
internal static class MediaHost
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== TeamsBot.Media Host ===");

        var fqdn           = GetRequiredEnv("MEDIA_SERVICE_FQDN");
        var certThumbprint = GetRequiredEnv("MEDIA_CERT_THUMBPRINT");
        var publicPort     = int.Parse(Environment.GetEnvironmentVariable("MEDIA_PUBLIC_PORT") ?? "8445");
        var privatePort    = int.Parse(Environment.GetEnvironmentVariable("MEDIA_PRIVATE_PORT") ?? "8445");
        var speechKey      = GetRequiredEnv("AZURE_SPEECH_KEY");
        var speechRegion   = GetRequiredEnv("AZURE_SPEECH_REGION");
        var appId          = GetRequiredEnv("MICROSOFT_APP_ID");

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("[MediaHost] Shutdown requested...");
        };

        // Create components
        using var pipeServer  = new PipeServer();
        var transcriber       = new SpeechTranscriber(speechKey, speechRegion, pipeServer);
        var mediaPlatform     = new MediaPlatformService(appId, fqdn, certThumbprint, publicPort, privatePort, transcriber);
        using var apiServer   = new MediaApiServer(mediaPlatform);

        try
        {
            // Initialize the RMP media platform
            mediaPlatform.Initialize();

            // Start the named pipe server (runs forever, reconnects clients)
            var pipeTask = Task.Run(() => pipeServer.StartAsync(cts.Token), cts.Token);

            // Start the local HTTP API server (TeamsBot.Web calls this to get media blobs)
            var apiTask  = Task.Run(() => apiServer.RunAsync(cts.Token), cts.Token);

            Console.WriteLine("[MediaHost] Running. Press Ctrl+C to stop.");

            // Keep alive until cancelled
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MediaHost] Fatal error: {ex}");
            return 1;
        }
        finally
        {
            Console.WriteLine("[MediaHost] Shutting down...");
            mediaPlatform.Dispose();
            await transcriber.DisposeAsync();
            Console.WriteLine("[MediaHost] Shutdown complete.");
        }

        return 0;
    }

    private static string GetRequiredEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Environment variable {name} is required but not set.");
}
