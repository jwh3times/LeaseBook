using Microsoft.Extensions.Logging;

namespace LeaseBook.Tests.Integration.Observability;

/// <summary>Captures log entries so tests can assert that detail withheld from the HTTP
/// response actually reached the log.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, eventId, formatter(state, exception), exception));
}

/// <summary>
/// Captures every category's entries in one list, for assertions against code that resolves its logger
/// from a running host's container rather than taking one as a constructor argument. Attach with
/// <c>host.Services.GetRequiredService&lt;ILoggerFactory&gt;().AddProvider(provider)</c> before the code
/// under test runs; <c>LoggerFactory</c> back-fills already-created loggers, so ordering against host
/// startup does not matter.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<(LogLevel Level, string Category, string Message)> _entries = [];

    /// <summary>A snapshot — the host's background services keep logging on other threads.</summary>
    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogLevel level, string category, string message)
    {
        lock (_entries)
        {
            _entries.Add((level, category, message));
        }
    }

    private sealed class Sink(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Add(logLevel, category, formatter(state, exception));
    }
}
