using System.Diagnostics;
using LeaseBook.SharedKernel.Observability;
using Microsoft.Extensions.Logging;

namespace LeaseBook.SharedKernel.Cqrs.Decorators;

/// <summary>
/// Outermost decorator (P24): opens an OpenTelemetry span named <c>cqrs.&lt;MessageName&gt;</c>
/// around the dispatch and records duration. The span flows to the Azure Monitor exporter when
/// the host has a connection string; locally there is no exporter, so it is a no-op beyond the
/// local log line.
/// </summary>
public sealed class TelemetryCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<TelemetryCommandDecorator<TCommand, TResult>> logger) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> Handle(TCommand command, CancellationToken ct)
    {
        var name = typeof(TCommand).Name;
        using var activity = LeaseBookTelemetry.Source.StartActivity($"cqrs.{name}");
        activity?.SetTag("cqrs.message_type", "command");
        activity?.SetTag("cqrs.message", name);

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
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Command {CommandName} failed after {ElapsedMs:F1} ms",
                name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            throw;
        }
    }
}
