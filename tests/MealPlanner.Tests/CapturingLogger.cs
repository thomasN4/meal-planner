using Microsoft.Extensions.Logging;

namespace MealPlanner.Tests;

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Records what was logged so the notifier's "swallow, but never silently"
/// contract can be asserted rather than assumed. Thread-safe: the notifier
/// fans out to subscribers that the concurrency tests run in parallel.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public IReadOnlyList<LogEntry> Errors =>
        Entries.Where(e => e.Level == LogLevel.Error).ToArray();

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var entry = new LogEntry(logLevel, formatter(state, exception), exception);
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }
}
