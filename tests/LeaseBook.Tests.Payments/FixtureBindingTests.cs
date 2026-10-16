using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Payments.Support.PaymentsHarness;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Payment work is done only for the organization's own fixture (ADR-046), and a row is acted on
/// only under the binding it was made under. A fixture can be corrected after payments exist: its
/// account or its bank may be pointed elsewhere, while every payment and payout keeps the account and
/// bank it was made under. Its generation cannot change, since every row holds a key to it.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class FixtureBindingTests(PostgresFixture fixture)
{
    private const string Rebound = "acct_rebound";

    public static TheoryData<string, string> EntryPoints()
    {
        var data = new TheoryData<string, string>();
        foreach (var entry in new[]
        {
            "quote", "submit", "receive", "claim", "complete", "fail", "retry", "close review", "waiting", "mark overdue",
            "receive payout evidence", "receive processor payout", "receive bank evidence", "evaluate", "mark failed", "close payout",
        })
        {
            data.Add(entry, "another organization");
            data.Add(entry, "a stale fixture");
        }
        return data;
    }

    // Each entry point is given what it needs to do some work, so that one which went on without
    // checking its binding would be seen doing it instead of refusing.
    private static async Task<Func<FixtureBinding, Task>> PrepareAsync(PaymentsHarness h, string entry, CancellationToken ct)
    {
        Task<PaymentOperation> Submit(FixtureBinding b, Guid key) =>
            h.Payments.SubmitAsync(b, h.Tenants[0], h.UserId, key, 100m, "USD", PaymentMethods.Ach, 0m, ct);
        async Task<(PaymentOperation Op, Guid Lease)> ClaimedAsync()
        {
            var op = await h.InOrg(() => Submit(h.Binding, UuidV7.NewId()), ct);
            return (op, (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct))!.LeaseClaimId!.Value);
        }
        async Task<Guid> PayoutAsync()
        {
            var op = await h.AcceptedAsync(100m, ct);
            return (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding,
                h.Payout("po_1", 100m, Line("1", "Payment", op, 100m, 0m, 100m)), ct), ct))!.Value;
        }

        switch (entry)
        {
            case "quote":
                return b => h.Payments.QuoteAsync(b, 100m, PaymentMethods.Ach, ct);
            case "submit":
                {
                    var key = UuidV7.NewId();
                    await h.InOrg(() => Submit(h.Binding, key), ct);
                    // The replay of a request already made, which finds it without asking anything else.
                    return b => Submit(b, key);
                }
            case "receive":
                {
                    var op = await h.AcceptedAsync(100m, ct);
                    return b => h.Payments.ReceiveAsync(b, h.Observation(op, "Processing"), ct);
                }
            case "claim":
                {
                    var op = await h.InOrg(() => Submit(h.Binding, UuidV7.NewId()), ct);
                    return b => h.Payments.ClaimAsync(b, op.Id, ct);
                }
            case "complete":
                {
                    var (op, lease) = await ClaimedAsync();
                    return b => h.Payments.CompleteAsync(b, op.Id, lease, new ProcessorResult("Accepted", ProviderOf(op)), ct);
                }
            case "fail":
                {
                    var (op, lease) = await ClaimedAsync();
                    return b => h.Payments.FailAsync(b, op.Id, lease, "technical_failure", ct);
                }
            case "retry":
                {
                    var op = await h.InOrg(() => Submit(h.Binding, UuidV7.NewId()), ct);
                    await h.InOrg(async () =>
                    {
                        var row = await h.Scope.Db.Set<PaymentOperation>().SingleAsync(x => x.Id == op.Id, ct);
                        row.Status = "NeedsReview"; row.Reason = "technical_failure";
                        await h.Scope.Db.SaveChangesAsync(ct);
                    }, ct);
                    return b => h.Payments.RetryAsync(b, op.Id, ct);
                }
            case "close review":
                {
                    var op = await h.AcceptedAsync(100m, ct);
                    await h.EmitAsync(h.Observation(op, "Dispute"), ct);
                    return b => h.Payments.CloseReviewAsync(b, op.Id, h.UserId, "Closed", ct);
                }
            case "waiting":
                await h.AcceptedAsync(100m, ct);
                return b => h.Payments.WaitingAsync(b, ct);
            case "mark overdue":
                {
                    var op = await h.AcceptedAsync(100m, ct);
                    h.Clock.Advance(PaymentEngine.OutcomeOverdueAfter + TimeSpan.FromHours(1));
                    return b => h.Payments.MarkOverdueAsync(b, op.Id, ct);
                }
            case "receive payout evidence":
                return b => h.Settlements.ReceiveAsync(b, h.Payout("po_1", 0m), ct);
            case "receive processor payout":
                return b => h.Settlements.ReceivePayoutAsync(b, h.ProcessorPayout("po_1", 0m), ct);
            case "receive bank evidence":
                await h.InOrg(() => h.Settlements.ReceivePayoutAsync(h.Binding, h.ProcessorPayout("po_1", 0m), ct), ct);
                return b => h.Settlements.ReceiveBankEvidenceAsync(b, h.Bank("po_1", 0m), ct);
            case "evaluate":
                {
                    var id = await PayoutAsync();
                    return b => h.Settlements.EvaluateAsync(b, id, null, ct);
                }
            case "mark failed":
                {
                    var id = await PayoutAsync();
                    return b => h.Settlements.MarkFailedAsync(b, id, ct);
                }
            default:
                {
                    var id = await PayoutAsync();
                    return b => h.Settlements.CloseAsync(b, id, h.UserId, "Closed", ct);
                }
        }
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task Every_entry_point_refuses_a_binding_that_is_not_the_organizations_fixture(string entry, string binding)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var act = await PrepareAsync(h, entry, ct);
        var other = h.Binding with { OrgId = UuidV7.NewId() };
        if (binding == "a stale fixture")
        {
            // The binding every row here was made under, held by a host after the fixture moved on.
            await h.RebindAsync(ct, account: Rebound);
            other = h.Binding;
        }

        await Should.ThrowAsync<PaymentUnavailableException>(() => h.InOrg(() => act(other), ct));
    }

    public static TheoryData<string, string> CallsOnAPayment()
    {
        var data = new TheoryData<string, string>();
        foreach (var call in new[] { "claim", "complete", "close review", "mark overdue" })
        {
            data.Add(call, "bank");
            data.Add(call, "account");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CallsOnAPayment))]
    public async Task A_payment_made_under_the_fixtures_earlier_bank_or_account_is_refused_under_the_new_one(string call, string moved)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        PaymentOperation op;
        Guid lease = default;
        switch (call)
        {
            case "claim":
                op = await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, UuidV7.NewId(), 100m, "USD",
                    PaymentMethods.Ach, 0m, ct), ct);
                break;
            case "complete":
                op = await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, UuidV7.NewId(), 100m, "USD",
                    PaymentMethods.Ach, 0m, ct), ct);
                lease = (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct))!.LeaseClaimId!.Value;
                break;
            case "close review":
                op = await h.AcceptedAsync(100m, ct);
                await h.EmitAsync(h.Observation(op, "Dispute"), ct);
                break;
            default:
                op = await h.AcceptedAsync(100m, ct);
                h.Clock.Advance(PaymentEngine.OutcomeOverdueAfter + TimeSpan.FromHours(1));
                break;
        }
        var before = await h.ReadAsync(op.Id, ct);
        var rebound = moved == "bank" ? await h.RebindAsync(ct, newBank: true) : await h.RebindAsync(ct, account: Rebound);

        await Should.ThrowAsync<PaymentUnavailableException>(() => h.InOrg(() => call switch
        {
            "claim" => h.Payments.ClaimAsync(rebound, op.Id, ct),
            "complete" => h.Payments.CompleteAsync(rebound, op.Id, lease, new ProcessorResult("Accepted", ProviderOf(op)), ct),
            "close review" => h.Payments.CloseReviewAsync(rebound, op.Id, h.UserId, "Closed", ct),
            _ => h.Payments.MarkOverdueAsync(rebound, op.Id, ct),
        }, ct));

        var after = await h.ReadAsync(op.Id, ct);
        (after.Status, after.Reason, after.LeaseClaimId, after.ProviderId).ShouldBe((before.Status, before.Reason, before.LeaseClaimId, before.ProviderId));
    }

    public static TheoryData<string> Moves() => ["bank", "account"];

    [Theory]
    [MemberData(nameof(Moves))]
    public async Task The_payments_waiting_under_a_rebound_fixture_leave_out_those_made_before_it_moved(string moved)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        await h.AcceptedAsync(100m, ct);
        var rebound = moved == "bank" ? await h.RebindAsync(ct, newBank: true) : await h.RebindAsync(ct, account: Rebound);

        (await h.InOrg(() => h.Payments.WaitingAsync(rebound, ct), ct)).ShouldBeEmpty();
    }

    public static TheoryData<string> ObservationFields() => ["generation", "account", "mode"];

    [Theory]
    [MemberData(nameof(ObservationFields))]
    public async Task A_notice_that_is_not_for_this_binding_is_refused_and_not_stored(string differs)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var notice = h.Observation(op, "Processing");
        notice = differs switch
        {
            "generation" => notice with { Generation = UuidV7.NewId() },
            "account" => notice with { Account = "acct_other" },
            _ => notice with { Mode = PaymentModes.StripeSandbox },
        };

        await Should.ThrowAsync<PaymentUnavailableException>(() => h.ObserveAsync(notice, ct));

        (await h.ReadObservationsAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_notice_under_a_rebound_account_is_its_own_though_an_earlier_account_used_its_event_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var progress = h.Observation(op, "Processing");
        await h.ObserveAsync(progress, ct);
        var rebound = await h.RebindAsync(ct, account: Rebound);

        await h.InOrg(() => h.Payments.ReceiveAsync(rebound, progress with { Account = Rebound, ProviderId = "prov_new" }, ct), ct);

        var stored = (await h.ReadObservationsAsync(ct)).Single(x => x.Account == Rebound);
        (stored.EventId, stored.Kind, stored.ProviderId).ShouldBe((progress.EventId, "Processing", "prov_new"));
    }

    [Fact]
    public async Task A_payout_under_a_rebound_account_is_its_own_though_an_earlier_account_used_its_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var first = (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, h.Payout("po_1", 0m), ct), ct))!.Value;
        var rebound = await h.RebindAsync(ct, account: Rebound);

        var second = await h.InOrg(() => h.Settlements.ReceiveAsync(rebound, h.Payout("po_1", 0m) with { Account = Rebound }, ct), ct);

        second.ShouldNotBeNull();
        second.ShouldNotBe(first);
        var earlier = await h.ReadSettlementAsync(first, ct);
        (earlier.Status, earlier.Reason).ShouldBe(("Received", (string?)null));
    }

    // A payment under the rebound binding, accepted under the given reference.
    private static async Task<PaymentOperation> AcceptedUnderAsync(PaymentsHarness h, FixtureBinding binding, string reference, CancellationToken ct)
    {
        var op = await h.InOrg(() => h.Payments.SubmitAsync(binding, h.Tenants[1], h.UserId, UuidV7.NewId(), 100m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);
        var lease = (await h.InOrg(() => h.Payments.ClaimAsync(binding, op.Id, ct), ct))!.LeaseClaimId!.Value;
        await h.InOrg(() => h.Payments.CompleteAsync(binding, op.Id, lease, new ProcessorResult("Accepted", reference), ct), ct);
        return await h.ReadAsync(op.Id, ct);
    }

    [Fact]
    public async Task A_payment_ages_by_itself_though_an_earlier_accounts_payment_had_its_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var earlier = await h.AcceptedAsync(100m, ct);
        var rebound = await h.RebindAsync(ct, account: Rebound);
        var op = await AcceptedUnderAsync(h, rebound, ProviderOf(earlier), ct);
        h.Clock.Advance(PaymentEngine.OutcomeOverdueAfter + TimeSpan.FromHours(1));

        (await h.InOrg(() => h.Payments.MarkOverdueAsync(rebound, op.Id, ct), ct)).ShouldBeTrue();

        (await h.ReadAsync(op.Id, ct)).Reason.ShouldBe("outcome_overdue");
        (await h.ReadAsync(earlier.Id, ct)).Status.ShouldBe("Processing");
    }

    [Fact]
    public async Task A_payout_under_a_rebound_account_settles_its_own_payment_though_an_earlier_accounts_payment_had_its_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var earlier = await h.AcceptedAsync(100m, ct);
        var rebound = await h.RebindAsync(ct, account: Rebound);
        var op = await AcceptedUnderAsync(h, rebound, ProviderOf(earlier), ct);
        var evidence = h.Payout("po_1", 100m, Line("1", "Payment", earlier, 100m, 0m, 100m)) with { Account = Rebound };

        var id = (await h.InOrg(() => h.Settlements.ReceiveAsync(rebound, evidence, ct), ct))!.Value;
        var payout = await h.InOrg(() => h.Settlements.EvaluateAsync(rebound, id, null, ct), ct);

        (payout!.Status, payout.Reason).ShouldBe(("Posted", (string?)null));
        h.Ledger.Settlements.ShouldHaveSingleItem().Lines.ShouldBe([new("1", "Payment", h.Tenants[1], 100m, PaymentMethods.Ach, null, 0m)]);
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Settled");
        var untouched = await h.ReadAsync(earlier.Id, ct);
        (untouched.Status, untouched.JournalId).ShouldBe(("Processing", (Guid?)null));
    }

    [Fact]
    public async Task A_payout_judges_its_payment_by_what_was_said_under_its_own_account_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var rebound = await h.RebindAsync(ct, account: Rebound);
        var op = await AcceptedUnderAsync(h, rebound, "prov_shared", ct);
        // A reference is unique per account (the unique index on org, account and provider id), so a
        // failure reported under the earlier account with the same reference is about another payment.
        var earlier = await h.RebindAsync(ct, account: h.Binding.Account);
        await h.InOrg(() => h.Payments.ReceiveAsync(earlier, h.Observation(op, "Failed") with
        {
            ProviderId = "prov_shared",
            Account = earlier.Account,
        }, ct), ct);
        await h.RebindAsync(ct, account: Rebound);
        var evidence = h.Payout("po_1", 100m, new ProcessorSettlementItem("1", "Payment", "prov_shared", 100m, 0m, 100m, "USD"))
            with
        { Account = Rebound };

        var id = (await h.InOrg(() => h.Settlements.ReceiveAsync(rebound, evidence, ct), ct))!.Value;
        var payout = await h.InOrg(() => h.Settlements.EvaluateAsync(rebound, id, null, ct), ct);

        (payout!.Status, payout.Reason).ShouldBe(("Posted", (string?)null));
        h.Ledger.Settlements.ShouldHaveSingleItem().Lines.ShouldBe([new("1", "Payment", h.Tenants[1], 100m, PaymentMethods.Ach, null, 0m)]);
    }

    public static TheoryData<string> PayoutBindings() => ["mode", "account"];

    [Theory]
    [MemberData(nameof(PayoutBindings))]
    public async Task A_payout_is_checked_or_closed_only_under_the_binding_it_was_stored_under(string differs)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var id = (await h.InOrg(() => h.Settlements.ReceiveAsync(h.Binding, h.Payout("po_1", 0m), ct), ct))!.Value;
        var other = differs == "mode" ? h.Binding with { Mode = PaymentModes.StripeSandbox } : await h.RebindAsync(ct, account: Rebound);

        await Should.ThrowAsync<PaymentUnavailableException>(() => h.InOrg(() => h.Settlements.EvaluateAsync(other, id, UuidV7.NewId(), ct), ct));
        await Should.ThrowAsync<PaymentUnavailableException>(() => h.InOrg(() => h.Settlements.CloseAsync(other, id, h.UserId, "No", ct), ct));

        (await h.ReadSettlementAsync(id, ct)).Status.ShouldBe("Received");
    }
}
