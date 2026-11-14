using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// A vendor payment recorded from the bank register (#745): the bank-adjustment command's
/// <c>vendor-payment</c> kind posts <c>VendorPaid</c> against the owner the property belongs to on the
/// payment's date (resolved here, never sent by the client), from an operating trust account, dated no
/// later than the organization's today, and guarded at zero rather than at the owner's reserve.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class VendorPaymentCommandTests(PostgresFixture fixture)
{
    private static readonly DateOnly Feb5 = new(2026, 2, 5);
    private static readonly Leases NoLeases = new(new Dictionary<Guid, TenantPostingDimensions>());

    // Per test: directory ids are global, so each test's org seeds its own.
    private readonly Guid Owner = UuidV7.NewId();
    private readonly Guid OtherOwner = UuidV7.NewId();
    private readonly Guid Property = UuidV7.NewId();

    private PropertyOwners OwnedBy(Guid owner) => new(new Dictionary<Guid, Guid> { [Property] = owner });

    private RecordBankAdjustment Payment(Guid bank, decimal amount = 300m, string? reference = "1042", string? note = null) =>
        Payment(bank, Property, amount, reference, note);

    private static RecordBankAdjustment Payment(
        Guid bank, Guid property, decimal amount = 300m, string? reference = "1042", string? note = null) =>
        new("vendor-payment", amount, Feb5, bank, null, "Leak repair", "adj:vendor", note,
            PropertyId: property, Payee: "Acme Plumbing", Reference: reference);

    [Fact]
    public async Task A_payment_posts_against_the_propertys_owner_and_lowers_the_bank_by_its_amount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);

        var result = await SendAsync(scope, Payment(scope.TrustBankId, note: "invoice 88"), ct);

        (await ReadLinesAsync(scope, result.EntryId, ct)).ShouldBe(
        [
            new(AccountCodes.OwnerEquity, 300m, null, EntryBasis.Both, null, Owner, Property, null, scope.TrustBankId),
            new(AccountCodes.TrustBank(scope.TrustBankId), null, 300m, EntryBasis.Both,
                null, null, null, null, scope.TrustBankId),
        ], ignoreOrder: true);
        await scope.RunAsync(async () =>
        {
            var entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == result.EntryId, ct);
            (entry.EventType, entry.Description, entry.InternalNote, entry.EntryDate).ShouldBe(
                ("VendorPaid", "Vendor payment to Acme Plumbing — Leak repair · ref 1042", "invoice 88", Feb5));
            (await new InvariantChecks(scope.Db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
    }

    [Fact]
    public async Task A_payment_charges_whoever_owns_the_property_on_its_date()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner, OtherOwner], properties: [Property]);
        await FundAsync(scope, OtherOwner, 1000m, ct);

        var result = await SendAsync(scope, Payment(scope.TrustBankId, reference: null), ct, OwnedBy(OtherOwner));

        var lines = await ReadLinesAsync(scope, result.EntryId, ct);
        lines.Single(l => l.Code == AccountCodes.OwnerEquity).OwnerId.ShouldBe(OtherOwner);
        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.Id == result.EntryId).Select(e => e.Description).SingleAsync(ct))
            .ShouldBe("Vendor payment to Acme Plumbing — Leak repair"), ct);
    }

    [Fact]
    public async Task A_payment_may_spend_the_owners_whole_balance_whatever_their_reserve()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);

        await SendAsync(scope, Payment(scope.TrustBankId, 1000m), ct);

        await AssertEntryCountAsync(scope, 2, ct);
    }

    [Fact]
    public async Task A_payment_larger_than_the_owners_balance_is_refused_and_points_to_an_owner_contribution()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);

        var refused = await Should.ThrowAsync<ReserveFloorException>(
            () => SendAsync(scope, Payment(scope.TrustBankId, 1000.01m), ct));

        (refused.Code, refused.Reserve, refused.OwnerId).ShouldBe(("reserve_floor", 0m, Owner));
        refused.Message.ShouldEndWith("Record an owner contribution first to cover it.");
        await AssertEntryCountAsync(scope, 1, ct);
    }

    [Fact]
    public async Task A_property_with_no_owner_on_the_date_is_refused_as_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);
        var stranger = UuidV7.NewId();

        var refused = await Should.ThrowAsync<PropertyNotFoundException>(
            () => SendAsync(scope, Payment(scope.TrustBankId, stranger), ct));

        (refused.Code, refused.PropertyId).ShouldBe(("property_not_found", stranger));
        await AssertEntryCountAsync(scope, 1, ct);
    }

    [Fact]
    public async Task A_payment_dated_after_the_organizations_today_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);

        var refused = await Should.ThrowAsync<PostingDateInFutureException>(() => SendAsync(scope,
            Payment(scope.TrustBankId), ct, clock: Noon(Feb5.AddDays(-1))));

        refused.Code.ShouldBe("posting_date_in_future");
        await AssertEntryCountAsync(scope, 1, ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_payment_from_a_bank_that_is_not_operating_trust_is_refused(bool pmOperating)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FundedScopeAsync(1000m, ct);
        var bank = pmOperating ? scope.OperatingBankId : scope.DepositBankId;

        var refused = await Should.ThrowAsync<VendorPaymentBankNotOperatingTrustException>(
            () => SendAsync(scope, Payment(bank), ct));

        (refused.Code, refused.BankAccountId).ShouldBe(("vendor_payment_bank_not_operating_trust", bank));
        await AssertEntryCountAsync(scope, 1, ct);
    }

    // ---- validation ----

    private const string NoProperty = "A vendor payment names the property it is for.";
    private const string NoPayee = "A vendor payment names who was paid.";
    private const string NoDescription = "A vendor payment says what it was for.";
    private const string NotAVendorPayment = "Only a vendor payment names a payee or a reference.";

    private static readonly RecordBankAdjustment Valid = Payment(UuidV7.NewId(), UuidV7.NewId());

    private static readonly Dictionary<string, (RecordBankAdjustment Command, string[] Properties, string Message)> Cases = new()
    {
        ["PropertyId.Null"] = (Valid with { PropertyId = null }, ["PropertyId"], NoProperty),
        ["PropertyId.Empty"] = (Valid with { PropertyId = Guid.Empty }, ["PropertyId"], NoProperty),
        ["PropertyId.Upper"] = (Valid with { Kind = "VENDOR-PAYMENT", PropertyId = null }, ["PropertyId"], NoProperty),
        ["Payee.Null"] = (Valid with { Payee = null }, ["Payee"], NoPayee),
        ["Payee.Blank"] = (Valid with { Payee = "  " }, ["Payee"], NoPayee),
        ["Payee.Long"] = (Valid with { Payee = new string('a', 201) }, ["Payee"],
            "A vendor payment's payee is at most 200 characters."),
        ["Reference.Long"] = (Valid with { Reference = new string('1', 101) }, ["Reference"],
            "A vendor payment's reference is at most 100 characters."),
        ["Description.Null"] = (Valid with { Description = null }, ["Description"], NoDescription),
        ["Description.Blank"] = (Valid with { Description = " " }, ["Description"], NoDescription),
        ["OwnerId"] = (Valid with { OwnerId = UuidV7.NewId() }, ["OwnerId"],
            "A vendor payment is charged to the property's owner on its date, so it names no owner."),
        ["Fee.Payee"] = (Valid with { Kind = "fee", PropertyId = null, Reference = null }, ["Payee"], NotAVendorPayment),
        ["Fee.Reference"] = (Valid with { Kind = "fee", PropertyId = null, Payee = null }, ["Reference"], NotAVendorPayment),
        ["Fee.PropertyId"] = (Valid with { Kind = "fee", Payee = null, Reference = null }, ["PropertyId"],
            "Only an owner contribution or a vendor payment names a property."),
    };

    public static TheoryData<string> Refusals() => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_malformed_vendor_payment_is_refused_by_its_own_rule(string name)
    {
        var (command, properties, message) = Cases[name];
        // A context already disposed: a request that got past its validator would fail on its first read.
        AppDbContext db = fixture.CreateContext(fixture.AppConnectionString, new OrgContext(), new ActorContext());
        await db.DisposeAsync();
        var sender = AccountingSender.Create(db, new OrgContext(), new ActorContext(), NoLeases);

        var refused = await Should.ThrowAsync<ValidationException>(() => sender.Send(command));

        refused.Errors.Select(e => e.PropertyName).ShouldBe(properties);
        refused.Errors.First().ErrorMessage.ShouldBe(message);
    }

    private async Task<OrgScope> FundedScopeAsync(decimal funds, CancellationToken ct)
    {
        var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);
        await FundAsync(scope, Owner, funds, ct);
        return scope;
    }

    private Task FundAsync(OrgScope scope, Guid owner, decimal funds, CancellationToken ct) =>
        scope.RunAsync(() => Events(scope).PostAsync(new OwnerContribution(
            owner, Property, new Money(funds), new DateOnly(2026, 2, 1), scope.TrustBankId, "fund"), ct), ct);

    private async Task<PostResult> SendAsync(
        OrgScope scope, RecordBankAdjustment command, CancellationToken ct,
        IPropertyOwnership? ownership = null, TimeProvider? clock = null)
    {
        PostResult result = null!;
        await scope.RunAsync(async () => result = await AccountingSender
            .Create(scope, NoLeases, clock ?? Noon(new DateOnly(2026, 3, 1)), ownership: ownership ?? OwnedBy(Owner))
            .Send(command, ct), ct);
        return result;
    }

    private static StoppedClock Noon(DateOnly day) => new(new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));

    private static Task AssertEntryCountAsync(OrgScope scope, int expected, CancellationToken ct) =>
        scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().CountAsync(ct)).ShouldBe(expected), ct);
}
