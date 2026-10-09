using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Payments;

/// <summary>
/// What became of delivered payout evidence: stored under <see cref="Id"/>; not for any fixture here
/// (no id); or <see cref="Malformed"/>, which the sender is told so that it does not count as delivered.
/// </summary>
public sealed record PayoutDelivery(Guid? Id, bool Malformed);

/// <summary>Owns the transaction boundaries around the transport seam, including failure bookkeeping.</summary>
public sealed class PaymentRunner(IServiceScopeFactory scopes, SimulationSettings settings,
    IPaymentProcessor processor, ILogger<PaymentRunner> log, TimeProvider clock)
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    // Stripe keeps an event for thirty days. A day inside that, so that the oldest asked for is still there.
    private static readonly TimeSpan EventsKept = TimeSpan.FromDays(29);
    // When each fixture was last swept, by organization. In memory on purpose: a host that restarts
    // sweeps at once, which is when a notice is most likely to have been missed.
    private readonly ConcurrentDictionary<Guid, DateTime> _sweptAt = new();

    public async Task RunOnceAsync(CancellationToken ct)
    {
        // One fixture's failure is kept until every fixture has had its turn, and thrown then: the
        // caller still hears of it, and the fixtures bound after it are not held up by it.
        ExceptionDispatchInfo? failure = null;
        foreach (var binding in settings.Fixtures.Where(_ => settings.Enabled))
        {
            try
            {
                var ids = await InOrg(binding, async sp =>
                {
                    await sp.GetRequiredService<PaymentEngine>().RequireFixtureAsync(binding, ct);
                    return await sp.GetRequiredService<AppDbContext>().Set<PaymentOperation>().AsNoTracking()
                        .Where(x => x.DueAt <= clock.GetUtcNow().UtcDateTime)
                        .OrderBy(x => x.DueAt).Select(x => x.Id).Take(100).ToListAsync(ct);
                }, ct);
                foreach (var id in ids) { await ProcessAsync(binding, id, ct); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { failure ??= ExceptionDispatchInfo.Capture(ex); }
        }
        failure?.Throw();
    }

    public async Task ProcessAsync(FixtureBinding binding, Guid id, CancellationToken ct)
    {
        var operation = await InOrg(binding, sp => sp.GetRequiredService<PaymentEngine>().ClaimAsync(binding, id, ct), ct);
        if (operation is null) { return; }
        var started = Stopwatch.GetTimestamp();
        try
        {
            var request = ProcessorRequest.For(binding, operation);
            var result = await processor.LookupAsync(request, ct);
            if (result.Outcome == "Absent") { result = await processor.SubmitAsync(request, ct); }
            await InOrg(binding, async sp =>
            {
                await sp.GetRequiredService<PaymentEngine>().CompleteAsync(binding, id, operation.LeaseClaimId!.Value, result, ct);
                return true;
            }, ct);
            log.LogInformation(new EventId(4600, "PaymentProcessed"),
                "Simulated payment {OperationId} processed in {ElapsedMs} ms", id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var reason = ex switch
            {
                AccountingDomainException domain => domain.Code is "period_closed" or "account_period_locked"
                    ? "accounting_period_locked" : "accounting_rejected",
                ValidationException => "attribution_unavailable",
                PaymentConflictException => "conflicting_evidence",
                PaymentUnavailableException => "fixture_unavailable",
                _ => "technical_failure",
            };
            // InOrg has disposed the failed transaction/context before recording this attempt.
            await InOrg(binding, async sp =>
            {
                await sp.GetRequiredService<PaymentEngine>().FailAsync(binding, id, operation.LeaseClaimId!.Value, reason, ct);
                return true;
            }, ct);
            log.LogWarning(new EventId(4601, "PaymentNeedsAttention"),
                "Simulated payment {OperationId} attempt ended with {Reason}", id, reason);
        }
    }

    /// <summary>
    /// Recovers what a lost notice would otherwise leave waiting for ever. For each fixture, at most
    /// once in <see cref="SweepInterval"/>: asks the processor for everything it has said since the
    /// oldest payment still waiting was made, keeps it as a callback's notice is kept, and only then
    /// sends to a person the payments that have waited too long. Nothing here throws but a
    /// cancellation, so a sweep that fails never costs the worker a pass.
    /// </summary>
    public async Task RecoverOnceAsync(CancellationToken ct)
    {
        foreach (var binding in settings.Fixtures.Where(_ => settings.Enabled))
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (_sweptAt.TryGetValue(binding.OrgId, out var last) && now - last < SweepInterval) { continue; }
            // Set before the attempt, so that one that fails is made again at the interval and not on every pass.
            _sweptAt[binding.OrgId] = now;
            try { await RecoverAsync(binding, now, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.LogWarning(new EventId(4607, "PaymentRecoveryFailed"),
                    "Payment recovery sweep failed ({ExceptionType}); it will be made again, and no payment was aged", ex.GetType().Name);
            }
        }
    }

    private async Task RecoverAsync(FixtureBinding binding, DateTime now, CancellationToken ct)
    {
        var waiting = await InOrg(binding, sp => sp.GetRequiredService<PaymentEngine>().WaitingAsync(binding, ct), ct);
        // Nothing waits, so nothing can have been missed: the processor is asked nothing.
        if (waiting.Count == 0) { return; }
        // Everything since the oldest payment still waiting, each time. That is what a stored cursor
        // would be for, without one to keep or to lose.
        var oldest = waiting[0].CreatedAt;
        var recovery = await processor.RecoverObservationsAsync(binding, oldest > now - EventsKept ? oldest : now - EventsKept, ct);
        // One organization transaction each, as for a callback. One already held is dropped there.
        foreach (var observation in recovery.Observations) { await ReceiveAsync(observation, ct); }
        log.LogInformation(new EventId(4606, "PaymentRecoverySwept"),
            "Payment recovery sweep listed {Listed} events: {Kept} stored or already held, {Unreadable} unreadable",
            recovery.Listed, recovery.Observations.Count, recovery.Unreadable);
        // Only here, after a sweep that was answered in full: a payment must never go to a person
        // while the events that could end it have not been asked for. One the sweep has just found
        // an event for is due, and the engine leaves it to the worker.
        foreach (var payment in waiting.Where(x => x.CreatedAt <= now - PaymentEngine.OutcomeOverdueAfter))
        {
            try
            {
                if (!await InOrg(binding, sp => sp.GetRequiredService<PaymentEngine>().MarkOverdueAsync(binding, payment.Id, ct), ct)) { continue; }
                log.LogWarning(new EventId(4601, "PaymentNeedsAttention"),
                    "Simulated payment {OperationId} sent to review with {Reason}", payment.Id, "outcome_overdue");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Its own transaction, rolled back. The others are aged all the same, and this one is offered again at the next sweep.
                log.LogWarning(new EventId(4601, "PaymentNeedsAttention"),
                    "Simulated payment {OperationId} could not be sent to review as overdue ({ExceptionType})", payment.Id, ex.GetType().Name);
            }
        }
    }

    /// <summary>An authentic notice the processor adapter says is not this host's to act on.</summary>
    public void Ignore() =>
        log.LogInformation(new EventId(4602, "PaymentCallbackIgnored"), "Payment notice for no fixture here ignored");

    public async Task ReceiveAsync(ProcessorObservation observation, CancellationToken ct)
    {
        var binding = settings.ForAccount(observation.Account);
        if (binding is null || observation.Mode != binding.Mode || observation.Generation != binding.Generation
            || !PaymentEngine.EventKinds.Contains(observation.Kind))
        {
            log.LogInformation(new EventId(4602, "PaymentCallbackIgnored"), "Unmapped simulated payment observation ignored");
            return;
        }
        await InOrg(binding, async sp =>
        {
            await sp.GetRequiredService<PaymentEngine>().ReceiveAsync(binding, observation, ct);
            return true;
        }, ct);
    }

    /// <summary>
    /// Stores a payout's evidence, then checks and posts it. Two transactions on purpose: the evidence
    /// is durable even when the posting attempt fails, and a refusal is recorded on the stored payout.
    /// </summary>
    public async Task<PayoutDelivery> ReceiveSettlementAsync(ProcessorSettlement evidence, CancellationToken ct)
    {
        var binding = settings.ForAccount(evidence.Account);
        if (binding is null || evidence.Mode != binding.Mode || evidence.Generation != binding.Generation)
        {
            log.LogInformation(new EventId(4602, "PaymentCallbackIgnored"), "Unmapped simulated payout evidence ignored");
            return new(null, false);
        }
        if (await InOrg(binding, sp => sp.GetRequiredService<SettlementEngine>().ReceiveAsync(binding, evidence, ct), ct) is not { } id)
        {
            log.LogInformation(new EventId(4602, "PaymentCallbackIgnored"), "Malformed simulated payout evidence refused");
            return new(null, true);
        }
        try
        {
            var settlement = await InOrg(binding, sp => sp.GetRequiredService<SettlementEngine>().EvaluateAsync(binding, id, null, ct), ct);
            log.LogInformation(new EventId(4604, "PaymentSettlementEvaluated"),
                "Simulated payout {SettlementId} is {Status} ({Reason})", id, settlement?.Status, settlement?.Reason ?? "none");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (PaymentConflictException)
        {
            // A redelivery of a payout already closed or marked as conflicting: nothing more to do.
        }
        catch (Exception ex)
        {
            // The evidence is stored and the failed attempt rolled back. Say so on the payout, so that
            // it shows as waiting with both ways out instead of sitting unseen as merely received.
            await InOrg(binding, async sp =>
            {
                await sp.GetRequiredService<SettlementEngine>().MarkFailedAsync(binding, id, ct);
                return true;
            }, ct);
            log.LogWarning(new EventId(4605, "PaymentSettlementNeedsAttention"),
                "Simulated payout {SettlementId} could not be checked ({ExceptionType})", id, ex.GetType().Name);
        }
        return new(id, false);
    }

    private async Task<T> InOrg<T>(FixtureBinding binding, Func<IServiceProvider, Task<T>> work, CancellationToken ct)
    {
        if (settings.ForOrg(binding.OrgId) != binding) { throw new PaymentUnavailableException(); }
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(binding.OrgId,
            "payments:worker", () => work(scope.ServiceProvider), ct);
    }
}

internal sealed class PaymentWorker(PaymentRunner runner, ILogger<PaymentWorker> logger, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Each on its own: what is due first, then the sweep, and neither kept from running by the other's failure.
            foreach (var step in new Func<CancellationToken, Task>[] { runner.RunOnceAsync, runner.RecoverOnceAsync })
            {
                try { await step(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogError(new EventId(4603, "PaymentWorkerUnavailable"),
                        "Payment worker unavailable ({ExceptionType}); durable work will be retried", ex.GetType().Name);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
        }
    }
}
