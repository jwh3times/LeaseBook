using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.Extensions.Logging;

namespace LeaseBook.Tests.SharedKernel.Support;

// Messages -----------------------------------------------------------------------------------

public sealed record PingCommand(string Text) : ICommand<string>;

public sealed record PingQuery(string Text) : IQuery<string>;

public sealed record TracedCommand(string Tag = "t") : ICommand<int>;

public sealed record GuardedCommand(int Value) : ICommand<int>;

/// <summary>Deliberately has no registered handler — exercises the unknown-message path.</summary>
public sealed record OrphanCommand : ICommand<string>;

// Handlers -----------------------------------------------------------------------------------

public sealed class PingCommandHandler : ICommandHandler<PingCommand, string>
{
    public Task<string> Handle(PingCommand command, CancellationToken ct) =>
        Task.FromResult($"handled:{command.Text}");
}

public sealed class PingQueryHandler : IQueryHandler<PingQuery, string>
{
    public Task<string> Handle(PingQuery query, CancellationToken ct) =>
        Task.FromResult($"queried:{query.Text}");
}

public sealed class TracedCommandHandler(List<string> trace) : ICommandHandler<TracedCommand, int>
{
    public Task<int> Handle(TracedCommand command, CancellationToken ct)
    {
        lock (trace)
        {
            trace.Add("handler");
        }

        return Task.FromResult(42);
    }
}

public sealed class GuardedCommandHandler(List<string> trace) : ICommandHandler<GuardedCommand, int>
{
    public Task<int> Handle(GuardedCommand command, CancellationToken ct)
    {
        lock (trace)
        {
            trace.Add("handler");
        }

        return Task.FromResult(command.Value);
    }
}

// Validators ---------------------------------------------------------------------------------

public sealed class TracedCommandValidator : AbstractValidator<TracedCommand>
{
    public TracedCommandValidator(List<string> trace) =>
        RuleFor(c => c.Tag).Must(_ =>
        {
            lock (trace)
            {
                trace.Add("validate");
            }

            return true;
        });
}

public sealed class GuardedCommandValidator : AbstractValidator<GuardedCommand>
{
    public GuardedCommandValidator() =>
        RuleFor(c => c.Value).GreaterThan(0).WithMessage("Value must be positive.");
}

// Capturing logger: appends every rendered log message to the shared trace so the test can
// observe where telemetry sits relative to validation and the handler.

public sealed class TraceLoggerProvider(List<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TraceLogger(sink);

    public void Dispose()
    {
    }

    private sealed class TraceLogger(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (sink)
            {
                sink.Add(formatter(state, exception));
            }
        }
    }
}
