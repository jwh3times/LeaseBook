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
