using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LeaseBook.SharedKernel.Cqrs.Decorators;

/// <summary>
/// Outermost decorator (P24): records the command name and duration. M0 logs locally; WP-05
/// rebases this onto OpenTelemetry (an Activity named <c>cqrs.&lt;MessageName&gt;</c>).
/// </summary>
public sealed class TelemetryCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<TelemetryCommandDecorator<TCommand, TResult>> logger) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> Handle(TCommand command, CancellationToken ct)
    {
        var name = typeof(TCommand).Name;
        logger.LogDebug("Dispatching command {CommandName}", name);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await inner.Handle(command, ct);
            logger.LogInformation("Command {CommandName} completed in {ElapsedMs:F1} ms",
                name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Command {CommandName} failed after {ElapsedMs:F1} ms",
                name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            throw;
        }
    }
}
