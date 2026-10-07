using System.Diagnostics;
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
    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var binding in settings.Fixtures.Where(_ => settings.Enabled))
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
            try { await runner.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(new EventId(4603, "PaymentWorkerUnavailable"),
                    "Payment worker unavailable ({ExceptionType}); durable work will be retried", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
        }
    }
}
