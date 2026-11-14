using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The <c>VendorPaid</c> template pays a vendor from an owner's money in an operating trust account only
/// (#745, ADR-014 addendum). The owner's money is held there: a security-deposit trust holds tenants'
/// deposits and the PM operating account is outside trust, so the template refuses both whoever posts
/// it. A payment the owner's balance cannot cover is refused in words that name a vendor payment and say
/// how to cover it.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class VendorPaymentTemplateTests(PostgresFixture fixture)
{
    // Per test: directory ids are global, so each test's org seeds its own.
    private readonly Guid Owner = UuidV7.NewId();
    private readonly Guid Property = UuidV7.NewId();
    private static readonly DateOnly Date = new(2026, 2, 6);

    [Fact]
    public async Task A_payment_names_the_payee_and_its_reference_and_keeps_the_staff_note()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);

        var id = await PostAsync(scope, Payment(scope.TrustBankId, 240m, reference: "1042", internalNote: "staff only"), ct);

        (await ReadLinesAsync(scope, id, ct)).ShouldBe(
        [
            new(AccountCodes.OwnerEquity, 240m, null, EntryBasis.Both, null, Owner, Property, null, scope.TrustBankId),
            new(AccountCodes.TrustBank(scope.TrustBankId), null, 240m, EntryBasis.Both,
                null, null, null, null, scope.TrustBankId),
        ], ignoreOrder: true);
        JournalEntry? entry = null;
        await scope.RunAsync(async () => entry = await scope.Db.Set<JournalEntry>()
            .AsNoTracking().SingleAsync(e => e.Id == id, ct), ct);
        (entry!.Description, entry.InternalNote)
            .ShouldBe(("Vendor payment to Acme Plumbing — kitchen leak · ref 1042", "staff only"));
    }

    [Fact]
    public async Task A_payment_from_the_security_deposit_trust_is_refused_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);

        var error = await Should.ThrowAsync<VendorPaymentBankNotOperatingTrustException>(
            () => PostAsync(scope, Payment(scope.DepositBankId, 240m), ct));

        (error.Code, error.BankAccountId).ShouldBe(("vendor_payment_bank_not_operating_trust", scope.DepositBankId));
        (await EntryCountAsync(scope, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_payment_from_the_pm_operating_account_is_refused_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);

        var error = await Should.ThrowAsync<VendorPaymentBankNotOperatingTrustException>(
            () => PostAsync(scope, Payment(scope.OperatingBankId, 240m), ct));

        (error.Code, error.BankAccountId).ShouldBe(("vendor_payment_bank_not_operating_trust", scope.OperatingBankId));
        (await EntryCountAsync(scope, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_payment_from_an_id_that_is_no_bank_account_stays_an_unknown_account()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);

        var error = await Should.ThrowAsync<UnknownAccountException>(
            () => PostAsync(scope, Payment(UuidV7.NewId(), 240m), ct));

        error.Code.ShouldBe("unknown_account");
        (await EntryCountAsync(scope, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_payment_larger_than_the_owners_balance_names_a_vendor_payment_and_an_owner_contribution()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);

        var error = await Should.ThrowAsync<ReserveFloorException>(
            () => PostAsync(scope, Payment(scope.TrustBankId, 1000.01m), ct));

        (error.Code, error.Amount, error.Equity, error.Reserve).ShouldBe(("reserve_floor", 1000.01m, 1000m, 0m));
        error.Message.ShouldBe(
            "This vendor payment of 1000.01 would take the owner's balance from 1000.00 below zero. " +
            "Record an owner contribution first to cover it.");
        (await EntryCountAsync(scope, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_payment_of_the_whole_balance_posts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);

        await PostAsync(scope, Payment(scope.TrustBankId, 1000m), ct);

        (await EntryCountAsync(scope, ct)).ShouldBe(2);
    }

    private VendorPaid Payment(Guid bank, decimal amount, string? reference = null, string? internalNote = null) =>
        new(Owner, Property, new Money(amount), Date, bank, "Acme Plumbing", "kitchen leak",
            Reference: reference, InternalNote: internalNote);

    private async Task<OrgScope> FundedScopeAsync(decimal funds, CancellationToken ct)
    {
        var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);
        await PostAsync(scope, new OwnerContribution(
            Owner, Property, new Money(funds), new DateOnly(2026, 2, 1), scope.TrustBankId, "fund"), ct);
        return scope;
    }

    private static async Task<Guid> PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task<int> EntryCountAsync(OrgScope scope, CancellationToken ct)
    {
        var count = 0;
        await scope.RunAsync(async () => count = await scope.Db.Set<JournalEntry>().CountAsync(ct), ct);
        return count;
    }
}
