using System;
using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TeamsBot.Media;

/// <summary>
/// Named pipe server that sends transcript segments from the Media host
/// to the Web host. Writes newline-delimited JSON (NDJSON).
/// Re-creates the pipe if the client disconnects and reconnects.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private const string PipeName = "teamsbot-transcript";

    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _disposed;

    public async Task StartAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                _pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                Console.WriteLine("[PipeServer] Waiting for client connection...");
                await _pipe.WaitForConnectionAsync(ct);
                Console.WriteLine("[PipeServer] Client connected.");

                _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };

                // Hold connection open until it breaks or we're cancelled
                while (_pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeServer] Error: {ex.Message}");
            }
            finally
            {
                CleanupPipe();
            }

            if (!ct.IsCancellationRequested)
            {
                Console.WriteLine("[PipeServer] Client disconnected. Waiting for reconnect...");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// Send a JSON line to the connected client. No-op if no client is connected.
    /// </summary>
    public async Task SendAsync(string jsonLine)
    {
        if (_disposed) return;

        await _lock.WaitAsync();
        try
        {
            if (_writer is not null && _pipe is not null && _pipe.IsConnected)
            {
                await _writer.WriteLineAsync(jsonLine);
            }
        }
        catch (IOException)
        {
            // Client disconnected — will reconnect in StartAsync loop
            CleanupPipe();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void CleanupPipe()
    {
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _writer = null;
        _pipe = null;
    }

    public void Dispose()
    {
        _disposed = true;
        CleanupPipe();
        _lock.Dispose();
    }
}
