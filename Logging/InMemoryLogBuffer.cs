using Microsoft.Extensions.Logging;

namespace TeamsBot.Logging;

/// <summary>
/// Thread-safe circular buffer of log entries, surfaced at GET /debug/logs.
/// </summary>
public sealed class InMemoryLogBuffer
{
    public sealed record LogEntry(
        DateTimeOffset Timestamp,
        string         Level,
        string         Category,
        string         Message);

    private readonly Queue<LogEntry> _q    = new();
    private readonly object          _lock = new();
    private const    int             Cap   = 600;

    public void Add(LogEntry e)
    {
        lock (_lock)
        {
            if (_q.Count >= Cap) _q.Dequeue();
            _q.Enqueue(e);
        }
    }

    public IReadOnlyList<LogEntry> GetLast(int n)
    {
        lock (_lock) { return _q.TakeLast(n).ToList(); }
    }
}

public sealed class InMemoryLoggerProvider(InMemoryLogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string category) => new InMemoryLogger(buffer, category);
    public void Dispose() { }
}

public sealed class InMemoryLogger(InMemoryLogBuffer buffer, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel level) =>
        level >= LogLevel.Information ||
        (level >= LogLevel.Debug && category.StartsWith("TeamsBot", StringComparison.Ordinal));

    public void Log<TState>(LogLevel level, EventId id, TState state,
        Exception? ex, Func<TState, Exception?, string> fmt)
    {
        if (!IsEnabled(level)) return;

        var msg = fmt(state, ex);
        if (ex is not null) msg += $"  [{ex.GetType().Name}: {ex.Message}]";

        buffer.Add(new InMemoryLogBuffer.LogEntry(
            Timestamp: DateTimeOffset.UtcNow,
            Level:     MapLevel(level),
            Category:  category,
            Message:   msg));
    }

    private static string MapLevel(LogLevel l) => l switch
    {
        LogLevel.Trace       => "DEBUG",
        LogLevel.Debug       => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning     => "WARNING",
        LogLevel.Error       => "ERROR",
        LogLevel.Critical    => "CRITICAL",
        _                    => "DEBUG"
    };
}
