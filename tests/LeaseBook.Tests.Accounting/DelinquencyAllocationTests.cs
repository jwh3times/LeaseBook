using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

[Collection(nameof(DatabaseCollection))]
public sealed class DelinquencyAllocationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Charges_with_the_same_due_date_use_stable_journal_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = UuidV7.NewId();
        var propertyId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();
        const string firstRef = "rent:first";
        const string secondRef = "rent:second";
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [ownerId], tenants: [tenantId], properties: [propertyId]);

        RentObligationEntriesResponse result = null!;
        Guid secondEntryId = default;
        await scope.RunAsync(async () =>
        {
            var events = AccountingTestHarness.Events(scope);
            await events.PostAsync(new RentCharged(
                tenantId, propertyId, ownerId, null, new Money(100m),
                new DateOnly(2026, 1, 1), "First charge", firstRef), ct);
            secondEntryId = await events.PostAsync(new RentCharged(
                tenantId, propertyId, ownerId, null, new Money(100m),
                new DateOnly(2026, 1, 1), "Second charge", secondRef), ct);
            await events.PostAsync(new PaymentReceived(
                tenantId, propertyId, ownerId, new Money(100m),
                new DateOnly(2026, 1, 2), PaymentMethod.Ach, scope.TrustBankId,
                "Payment", "payment:first"), ct);

            result = await new GetRentObligationEntriesHandler(scope.Db).Handle(
                new GetRentObligationEntries([firstRef, secondRef], new DateOnly(2026, 1, 10)), ct);
        }, ct);

        var open = result.Rows.ShouldHaveSingleItem();
        open.EntryId.ShouldBe(secondEntryId);
        open.SourceRef.ShouldBe(secondRef);
    }

    [Fact]
    public async Task Aging_uses_the_charge_due_date_not_its_accounting_date()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = UuidV7.NewId();
        var propertyId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [ownerId], tenants: [tenantId], properties: [propertyId]);

        DelinquencyResponse result = null!;
        await scope.RunAsync(async () =>
        {
            await AccountingTestHarness.Events(scope).PostAsync(new RentCharged(
                tenantId, propertyId, ownerId, null, new Money(100m),
                new DateOnly(2026, 1, 1), "January rent", "rent:january",
                DueDate: new DateOnly(2026, 1, 15)), ct);

            result = await new GetDelinquencyAgingHandler(scope.Db).Handle(
                new GetDelinquencyAging(new DateOnly(2026, 2, 10)), ct);
        }, ct);

        var row = result.Rows.ShouldHaveSingleItem();
        row.D1_30.ShouldBe(100m);
        row.D31_60.ShouldBe(0m);
        row.OldestAgeDays.ShouldBe(26);
    }

    [Fact]
    public async Task Fully_paid_charge_is_absent_from_delinquency_aging()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = UuidV7.NewId();
        var propertyId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [ownerId], tenants: [tenantId], properties: [propertyId]);

        DelinquencyResponse result = null!;
        await scope.RunAsync(async () =>
        {
            var events = AccountingTestHarness.Events(scope);
            await events.PostAsync(new RentCharged(
                tenantId, propertyId, ownerId, null, new Money(100m),
                new DateOnly(2026, 1, 1), "January rent", "rent:january"), ct);
            await events.PostAsync(new PaymentReceived(
                tenantId, propertyId, ownerId, new Money(100m),
                new DateOnly(2026, 1, 5), PaymentMethod.Ach, scope.TrustBankId,
                "January payment", "payment:january"), ct);

            result = await new GetDelinquencyAgingHandler(scope.Db).Handle(
                new GetDelinquencyAging(new DateOnly(2026, 2, 1)), ct);
        }, ct);

        result.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reversed_payment_reopens_the_identified_rent_obligation()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = UuidV7.NewId();
        var propertyId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();
        const string rentRef = "rent:2026-01:lease=test";
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [ownerId], tenants: [tenantId], properties: [propertyId]);

        RentObligationEntriesResponse result = null!;
        Guid rentEntryId = default;
        await scope.RunAsync(async () =>
        {
            var events = AccountingTestHarness.Events(scope);
            rentEntryId = await events.PostAsync(new RentCharged(
                tenantId, propertyId, ownerId, null, new Money(100m),
                new DateOnly(2026, 1, 1), "January rent", rentRef), ct);
            var paymentEntryId = await events.PostAsync(new PaymentReceived(
                tenantId, propertyId, ownerId, new Money(100m),
                new DateOnly(2026, 1, 5), PaymentMethod.Ach, scope.TrustBankId,
                "January payment", "payment:january"), ct);
            await AccountingTestHarness.Reversal(scope).ReverseAsync(
                paymentEntryId, "Payment returned", new DateOnly(2026, 2, 1), ct);

            result = await new GetRentObligationEntriesHandler(scope.Db).Handle(
                new GetRentObligationEntries([rentRef], new DateOnly(2026, 2, 15)), ct);
        }, ct);

        var obligation = result.Rows.ShouldHaveSingleItem();
        obligation.EntryId.ShouldBe(rentEntryId);
        obligation.SourceRef.ShouldBe(rentRef);
    }

    [Fact]
    public async Task Partial_payment_reduces_the_oldest_due_charge_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = UuidV7.NewId();
        var propertyId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [ownerId], tenants: [tenantId], properties: [propertyId]);

        DelinquencyResponse result = null!;
        await scope.RunAsync(async () =>
        {
            var events = AccountingTestHarness.Events(scope);
            await events.PostAsync(new RentCharged(
                tenantId, propertyId, ownerId, null, new Money(100m),
                new DateOnly(2026, 1, 1), "January rent", "rent:january"), ct);
            await events.PostAsync(new RentCharged(
                tenantId, propertyId, ownerId, null, new Money(100m),
                new DateOnly(2026, 2, 1), "February rent", "rent:february"), ct);
            await events.PostAsync(new PaymentReceived(
                tenantId, propertyId, ownerId, new Money(150m),
                new DateOnly(2026, 2, 10), PaymentMethod.Ach, scope.TrustBankId,
                "Partial payment", "payment:february"), ct);

            result = await new GetDelinquencyAgingHandler(scope.Db).Handle(
                new GetDelinquencyAging(new DateOnly(2026, 2, 20)), ct);
        }, ct);

        var row = result.Rows.ShouldHaveSingleItem();
        row.D1_30.ShouldBe(50m);
        row.D31_60.ShouldBe(0m);
        row.Total.ShouldBe(50m);
        row.UnappliedCredit.ShouldBe(0m);
        row.OldestAgeDays.ShouldBe(19);
    }

    [Fact]
    public async Task General_credit_beyond_open_charges_is_unapplied_not_negative_aging()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = UuidV7.NewId();
        var propertyId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [ownerId], tenants: [tenantId], properties: [propertyId]);

        DelinquencyResponse result = null!;
        await scope.RunAsync(async () =>
        {
            var events = AccountingTestHarness.Events(scope);
            await events.PostAsync(new RentCharged(
                tenantId, propertyId, ownerId, null, new Money(100m),
                new DateOnly(2026, 1, 1), "January rent", "rent:january"), ct);
            await events.PostAsync(new CreditIssued(
                tenantId, propertyId, ownerId, new Money(125m),
                new DateOnly(2026, 1, 2), "General credit", "credit:january"), ct);

            result = await new GetDelinquencyAgingHandler(scope.Db).Handle(
                new GetDelinquencyAging(new DateOnly(2026, 2, 1)), ct);
        }, ct);

        var row = result.Rows.ShouldHaveSingleItem();
        row.Total.ShouldBe(0m);
        row.Current.ShouldBe(0m);
        row.D1_30.ShouldBe(0m);
        row.D31_60.ShouldBe(0m);
        row.D61_90.ShouldBe(0m);
        row.Over90.ShouldBe(0m);
        row.UnappliedCredit.ShouldBe(25m);
    }
}
