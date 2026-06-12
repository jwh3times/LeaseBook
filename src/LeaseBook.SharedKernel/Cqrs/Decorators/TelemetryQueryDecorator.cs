using System.Diagnostics;
using LeaseBook.SharedKernel.Observability;
using Microsoft.Extensions.Logging;

namespace LeaseBook.SharedKernel.Cqrs.Decorators;

/// <summary>Query counterpart of <see cref="TelemetryCommandDecorator{TCommand,TResult}"/>.</summary>
public sealed class TelemetryQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ILogger<TelemetryQueryDecorator<TQuery, TResult>> logger) : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    public async Task<TResult> Handle(TQuery query, CancellationToken ct)
    {
        var name = typeof(TQuery).Name;
        using var activity = LeaseBookTelemetry.Source.StartActivity($"cqrs.{name}");
        activity?.SetTag("cqrs.message_type", "query");
        activity?.SetTag("cqrs.message", name);

        logger.LogDebug("Dispatching query {QueryName}", name);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await inner.Handle(query, ct);
            logger.LogInformation("Query {QueryName} completed in {ElapsedMs:F1} ms",
                name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Query {QueryName} failed after {ElapsedMs:F1} ms",
                name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            throw;
        }
    }
}
