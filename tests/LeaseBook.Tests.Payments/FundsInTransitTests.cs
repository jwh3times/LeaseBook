using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Funds in transit (ADR-053): per tenant, the ledger amounts of payments the processor has reported as
/// collected that no bank evidence has yet answered for, with the earliest paid time and how many.
/// Nothing failed, settled, held for a person or still unpaid is on its way anywhere.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class FundsInTransitTests(PostgresFixture fixture)
{
    private static async Task<PaymentOperation> PaidAsync(PaymentsHarness h, decimal amount, DateTime paidAt, CancellationToken ct,
        int tenant = 0)
    {
        var op = await h.AcceptedAsync(amount, ct, tenant);
        await h.EmitAsync(h.Observation(op, "Succeeded") with { ObservedAt = paidAt }, ct);
        return op;
    }

    [Fact]
    public async Task Only_collected_payments_still_waiting_for_the_bank_are_in_transit_summed_per_tenant_from_the_earliest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var now = h.Clock.GetUtcNow().UtcDateTime;
        await PaidAsync(h, 100m, now.AddHours(-1), ct);
        await PaidAsync(h, 50m, now.AddHours(-2), ct);
        await PaidAsync(h, 25m, now.AddHours(-3), ct, tenant: 1);

        // Accepted and never reported paid.
        var accepted = await h.AcceptedAsync(70m, ct);
        // Paid, and then the bank showed it: it has arrived.
        var settled = await PaidAsync(h, 80m, now.AddHours(-4), ct);
        await h.EmitAsync(h.Observation(settled, "BankCredit"), ct);
        // Not yet sent to the processor.
        await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, UuidV7.NewId(), 90m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);
        // Reported both paid and failed: held for a person, with its paid time kept.
        var contradicted = await PaidAsync(h, 60m, now.AddHours(-5), ct, tenant: 1);
        await h.EmitAsync(h.Observation(contradicted, "Failed"), ct);
        (await h.ReadAsync(accepted.Id, ct)).Status.ShouldBe("Processing");
        (await h.ReadAsync(settled.Id, ct)).Status.ShouldBe("Settled");
        var held = await h.ReadAsync(contradicted.Id, ct);
        (held.Status, held.PaidAt).ShouldBe(("NeedsReview", (DateTime?)now.AddHours(-5)));

        var all = await h.QueryAsync(new GetFundsInTransit(), ct);

        all.ShouldBe([
            new TenantInTransit(h.Tenants[0], 150m, now.AddHours(-2), 2),
            new TenantInTransit(h.Tenants[1], 25m, now.AddHours(-3), 1),
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task Funds_in_transit_are_read_for_the_tenants_named_and_for_none_when_none_are_named()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var now = h.Clock.GetUtcNow().UtcDateTime;
        await PaidAsync(h, 100m, now.AddHours(-1), ct);
        await PaidAsync(h, 25m, now.AddHours(-3), ct, tenant: 1);

        (await h.QueryAsync(new GetFundsInTransit([h.Tenants[1]]), ct))
            .ShouldBe([new TenantInTransit(h.Tenants[1], 25m, now.AddHours(-3), 1)]);
        (await h.QueryAsync(new GetFundsInTransit([]), ct)).ShouldBeEmpty();
        (await h.QueryAsync(new GetFundsInTransit([UuidV7.NewId()]), ct)).ShouldBeEmpty();
    }
}
