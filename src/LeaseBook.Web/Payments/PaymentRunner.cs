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
    IPaymentProcessor processor, ILogger<PaymentRunner> log, TimeProvider clock, BankEvidenceSigner bankEvidence)
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    // Stripe keeps an event for thirty days. A day inside that, so that the oldest asked for is still there.
    private static readonly TimeSpan EventsKept = TimeSpan.FromDays(29);
    // How far before its last good sweep a sweep asks again: what the processor listed late, or dated
    // a little before it listed it, is then still asked for. What is read twice is kept once.
    private static readonly TimeSpan SweepOverlap = TimeSpan.FromMinutes(10);
    // When each fixture was last swept, by organization. In memory on purpose: a host that restarts
    // sweeps at once, which is when a notice is most likely to have been missed.
    private readonly ConcurrentDictionary<Guid, DateTime> _sweptAt = new();
    // When the last sweep that was kept whole began, by organization. In memory too: a host that
    // restarts has none, and asks for everything the processor still keeps.
    private readonly ConcurrentDictionary<Guid, DateTime> _keptFrom = new();

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
                "Online payment {OperationId} processed in {ElapsedMs} ms", id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
                "Online payment {OperationId} attempt ended with {Reason}", id, reason);
        }
    }

    /// <summary>
    /// Recovers what a lost notice would otherwise leave unknown for ever. For each fixture, at most
    /// once in <see cref="SweepInterval"/>, whether or not a payment is waiting: a dispute or a
    /// refund comes for a payment that stopped waiting long before. It asks the processor for
    /// everything it has said since a little before the last sweep that was kept whole, or since the
    /// oldest payment still waiting was made if that is earlier, keeps it as a callback's notice is
    /// kept, and only then sends to a person the payments that have waited too long. Nothing here
    /// throws but a cancellation, so a sweep that fails never costs the worker a pass.
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
        // From the last sweep that was kept whole, less the overlap; with none in this process, from
        // as far back as the processor keeps anything. A payment still waiting from before that may
        // have an outcome from before it, so the oldest of those reaches further back. Never past
        // what is kept.
        var kept = now - EventsKept;
        var since = _keptFrom.TryGetValue(binding.OrgId, out var last) ? last - SweepOverlap : kept;
        if (waiting.Count > 0 && waiting[0].CreatedAt < since) { since = waiting[0].CreatedAt; }
        var recovery = await processor.RecoverObservationsAsync(binding, since > kept ? since : kept, ct);
        // One organization transaction each, as for a callback. One already held is dropped there.
        foreach (var observation in recovery.Observations) { await ReceiveAsync(observation, ct); }
        // A payout found here is kept as a delivered one is, and held the same way. One already held is dropped there.
        foreach (var payout in recovery.Payouts) { await ReceivePayoutAsync(payout, ct); }
        // Only now, with everything found handed on and none of it refused: a sweep that failed
        // anywhere above leaves the time where it was, and the next one asks for the same again. So
        // does one that left an event unread because the processor would not answer about it.
        if (recovery.Unasked == 0) { _keptFrom[binding.OrgId] = now; }
        log.LogInformation(new EventId(4606, "PaymentRecoverySwept"),
            "Payment recovery sweep listed {Listed} events: {Kept} stored or already held, {Unreadable} unreadable, {Unasked} to be asked about again",
            recovery.Listed, recovery.Observations.Count + recovery.Payouts.Count, recovery.Unreadable, recovery.Unasked);
        // Only here, after a list that was answered in full: a payment must never go to a person
        // while the events that could end it have not been asked for. An event left to be asked
        // about again does not stand in the way. Only a dispute needs asking about, and a dispute
        // follows a success; aging is of payments with no outcome, whose own events need no asking.
        // One the sweep has just found an event for is due, and the engine leaves it to the worker.
        foreach (var payment in waiting.Where(x => x.CreatedAt <= now - PaymentEngine.OutcomeOverdueAfter))
        {
            try
            {
                if (!await InOrg(binding, sp => sp.GetRequiredService<PaymentEngine>().MarkOverdueAsync(binding, payment.Id, ct), ct)) { continue; }
                log.LogWarning(new EventId(4601, "PaymentNeedsAttention"),
                    "Online payment {OperationId} sent to review with {Reason}", payment.Id, "outcome_overdue");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Its own transaction, rolled back. The others are aged all the same, and this one is offered again at the next sweep.
                log.LogWarning(new EventId(4601, "PaymentNeedsAttention"),
                    "Online payment {OperationId} could not be sent to review as overdue ({ExceptionType})", payment.Id, ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// An authentic notice the processor adapter says is not this host's to act on. Stripe sends
    /// every kind of event an account has, so in a sandbox host this is most of what arrives and is
    /// said at Debug. The simulator's driver sends only what a fixture asked for, so there one is
    /// worth a line.
    /// </summary>
    public void Ignore() =>
        log.Log(settings.Mode == PaymentModes.StripeSandbox ? LogLevel.Debug : LogLevel.Information,
            new EventId(4602, "PaymentCallbackIgnored"), "Payment notice for no fixture here ignored");

    /// <summary>
    /// An authentic notice that could not be read because the processor would not answer what reading
    /// it needs. Nothing was kept and the sender was told to send it again. A library's or a
    /// transport's exception is named by its type only: a processor's own text can repeat part of a
    /// key. A condition the adapter worded itself is given as it stands, since that is what tells a
    /// notice that is early from one that will never be readable. Where the adapter handed on its
    /// library's failure, the type named is that failure's.
    /// </summary>
    public void Defer(Exception ex)
    {
        var unanswered = ex as ProcessorUnansweredException;
        log.LogWarning(new EventId(4608, "PaymentCallbackDeferred"),
            "Payment notice not read because the processor could not be asked about it ({ExceptionType}; adapter condition: {Condition}); refused so that it is sent again",
            (unanswered?.InnerException ?? ex).GetType().Name, unanswered?.Condition ?? "none");
    }

    public async Task ReceiveAsync(ProcessorObservation observation, CancellationToken ct)
    {
        var binding = settings.ForAccount(observation.Account);
        if (binding is null || observation.Mode != binding.Mode || observation.Generation != binding.Generation
            // A conflict is the sandbox adapter's to report. From any other processor it is no kind at all.
            || !(PaymentEngine.EventKinds.Contains(observation.Kind)
                || (observation.Kind == PaymentEngine.ConflictKind && binding.Mode == PaymentModes.StripeSandbox)))
        {
            log.LogInformation(new EventId(4602, "PaymentCallbackIgnored"), "Unmapped payment observation ignored");
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
            log.LogInformation(new EventId(4602, "PaymentCallbackIgnored"), "Unmapped payout evidence ignored");
            return new(null, false);
        }
        if (await InOrg(binding, sp => sp.GetRequiredService<SettlementEngine>().ReceiveAsync(binding, evidence, ct), ct) is not { } id)
        {
            log.LogInformation(new EventId(4602, "PaymentCallbackIgnored"), "Malformed payout evidence refused");
            return new(null, true);
        }
        await EvaluateAsync(binding, id, ct);
        return new(id, false);
    }

    /// <summary>
    /// Stores a processor's own account of a payout and checks it. Nothing posts: no bank has spoken.
    /// A payout whose lines could not post goes to a person now; any other waits for the bank. Two
    /// transactions, as for a payout that arrives with its evidence, so that what was stored stays
    /// stored when the check fails.
    /// </summary>
    public async Task<PayoutDelivery> ReceivePayoutAsync(ProcessorPayout payout, CancellationToken ct)
    {
        var binding = settings.ForAccount(payout.Account);
        if (binding is null || payout.Mode != binding.Mode || payout.Generation != binding.Generation)
        {
            log.LogInformation(new EventId(4602, "PaymentCallbackIgnored"), "Unmapped payout notice ignored");
            return new(null, false);
        }
        if (await InOrg(binding, sp => sp.GetRequiredService<SettlementEngine>().ReceivePayoutAsync(binding, payout, ct), ct) is not { } id)
        {
            log.LogInformation(new EventId(4602, "PaymentCallbackIgnored"), "Malformed payout notice refused");
            return new(null, true);
        }
        // A payout reported failed is with a person already, and there is nothing to check it against.
        if (payout.State == ProcessorPayoutStates.Paid) { await EvaluateAsync(binding, id, ct); }
        return new(id, false);
    }

    /// <summary>
    /// Takes bank evidence for a payout held for it: signed with the fixture key, in a Stripe sandbox
    /// host, for a payout a fixture here holds. Evidence that was exactly what a payout waited for is
    /// followed by the check and the posting, in a transaction of their own, as when a payout arrives
    /// with its evidence. Anything else changes nothing, or sends the payout to a person.
    /// </summary>
    public async Task<BankEvidenceDelivery> ReceiveBankEvidenceAsync(byte[] body, string? signature, CancellationToken ct)
    {
        if (bankEvidence.Verify(body, signature) is not { } notice || settings.ForAccount(notice.Account) is not { } binding
            || binding.Mode != PaymentModes.StripeSandbox)
        {
            log.LogInformation(new EventId(4602, "PaymentCallbackIgnored"), "Bank evidence that could not be verified refused");
            return new(BankEvidenceDelivery.Refused);
        }
        // The evidence's own reference says what it says, so that the same again is seen to be the same.
        var evidence = new BankEvidence(notice.PayoutId, binding.Account, binding.Mode, binding.Generation, notice.BankAmount, notice.BankDate,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"bank-{notice.BankDate:yyyyMMdd}-{notice.BankAmount:0.00}"));
        var result = await InOrg(binding, sp => sp.GetRequiredService<SettlementEngine>().ReceiveBankEvidenceAsync(binding, evidence, ct), ct);
        if (result.Id is not { } id) { return new(result.Outcome); }
        if (result.Evaluate) { await EvaluateAsync(binding, id, ct); }
        var settlement = await InOrg(binding, sp => sp.GetRequiredService<AppDbContext>().Set<PaymentSettlement>().AsNoTracking()
            .SingleAsync(x => x.Id == id, ct), ct);
        return new(result.Outcome, id, settlement.Status, settlement.Reason);
    }

    // Checks a stored payout and posts it if it can be posted, in a transaction apart from the one
    // that stored it. Never throws but for a cancellation: what it could not do is said on the payout.
    private async Task EvaluateAsync(FixtureBinding binding, Guid id, CancellationToken ct)
    {
        try
        {
            var settlement = await InOrg(binding, sp => sp.GetRequiredService<SettlementEngine>().EvaluateAsync(binding, id, null, ct), ct);
            log.LogInformation(new EventId(4604, "PaymentSettlementEvaluated"),
                "Payout {SettlementId} is {Status} ({Reason})", id, settlement?.Status, settlement?.Reason ?? "none");
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
                "Payout {SettlementId} could not be checked ({ExceptionType})", id, ex.GetType().Name);
        }
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
