using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
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
/// An owner contribution recorded from the bank register (#744): the bank-adjustment command's
/// <c>owner-contribution</c> kind posts <c>OwnerContribution</c> on the owner and the property it names,
/// into an operating trust account, dated no later than the organization's today, and only for a property
/// that belongs to that owner on that date.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OwnerContributionCommandTests(PostgresFixture fixture)
{
    private static readonly DateOnly Feb5 = new(2026, 2, 5);
    private static readonly Leases NoLeases = new(new Dictionary<Guid, TenantPostingDimensions>());

    // Per test: directory ids are global, so each test's org seeds its own (an id seeded into an earlier
    // test's org would leave this one's journal foreign keys without a row).
    private readonly Guid Owner = UuidV7.NewId();
    private readonly Guid OtherOwner = UuidV7.NewId();
    private readonly Guid Property = UuidV7.NewId();

    private PropertyOwners Owned => new(new Dictionary<Guid, Guid> { [Property] = Owner });

    private RecordBankAdjustment Contribution(Guid bank, string? description = null, string? note = null) =>
        Contribution(bank, Owner, Property, description, note);

    private static RecordBankAdjustment Contribution(
        Guid bank, Guid owner, Guid property, string? description = null, string? note = null) =>
        new("owner-contribution", 300m, Feb5, bank, null, description, "adj:owner", note, owner, property);

    [Fact]
    public async Task A_contribution_posts_on_the_owner_and_the_property_and_raises_the_bank_by_its_amount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);

        var result = await SendAsync(scope, Contribution(scope.TrustBankId, note: "check 1042"), ct);

        (await ReadLinesAsync(scope, result.EntryId, ct)).ShouldBe(
        [
            new(AccountCodes.TrustBank(scope.TrustBankId), 300m, null, EntryBasis.Both,
                null, null, null, null, scope.TrustBankId),
            new(AccountCodes.OwnerEquity, null, 300m, EntryBasis.Both,
                null, Owner, Property, null, scope.TrustBankId),
        ], ignoreOrder: true);
        await scope.RunAsync(async () =>
        {
            var entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == result.EntryId, ct);
            (entry.EventType, entry.Description, entry.InternalNote, entry.EntryDate)
                .ShouldBe(("OwnerContribution", "Owner contribution", "check 1042", Feb5));
            (await new InvariantChecks(scope.Db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
    }

    [Fact]
    public async Task A_contribution_keeps_the_description_staff_typed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);

        var result = await SendAsync(scope, Contribution(scope.TrustBankId, "Roof repair float"), ct);

        await scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.Id == result.EntryId).Select(e => e.Description).SingleAsync(ct))
            .ShouldBe("Roof repair float"), ct);
    }

    [Fact]
    public async Task A_property_that_belongs_to_another_owner_is_refused_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner, OtherOwner], properties: [Property]);
        var theirs = new PropertyOwners(new Dictionary<Guid, Guid> { [Property] = OtherOwner });

        var refused = await Should.ThrowAsync<PropertyNotOwnedException>(
            () => SendAsync(scope, Contribution(scope.TrustBankId), ct, theirs));

        (refused.Code, refused.PropertyId, refused.OwnerId, refused.Date).ShouldBe(("property_not_owned", Property, Owner, Feb5));
        await AssertNothingPostedAsync(scope, ct);
    }

    [Fact]
    public async Task A_property_the_owner_holds_only_from_a_later_date_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);
        var fromLater = new PropertyOwners(new Dictionary<Guid, Guid> { [Property] = Owner }, Feb5.AddDays(1));

        var refused = await Should.ThrowAsync<PropertyNotOwnedException>(
            () => SendAsync(scope, Contribution(scope.TrustBankId), ct, fromLater));

        refused.Code.ShouldBe("property_not_owned");
        await AssertNothingPostedAsync(scope, ct);
    }

    [Fact]
    public async Task A_contribution_dated_after_the_organizations_today_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);

        var refused = await Should.ThrowAsync<PostingDateInFutureException>(() => SendAsync(scope,
            Contribution(scope.TrustBankId), ct, clock: Noon(Feb5.AddDays(-1))));

        refused.Code.ShouldBe("posting_date_in_future");
        await AssertNothingPostedAsync(scope, ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_contribution_into_a_bank_that_is_not_operating_trust_is_refused(bool pmOperating)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);
        var bank = pmOperating ? scope.OperatingBankId : scope.DepositBankId;

        var refused = await Should.ThrowAsync<ContributionBankNotOperatingTrustException>(
            () => SendAsync(scope, Contribution(bank), ct));

        (refused.Code, refused.BankAccountId).ShouldBe(("contribution_bank_not_operating_trust", bank));
        await AssertNothingPostedAsync(scope, ct);
    }

    // ---- validation ----

    private const string NoOwner = "An owner contribution names the owner it comes from.";
    private const string NoProperty = "An owner contribution names the property it is for.";
    private const string NotAnOwnerContribution = "Only an owner contribution names an owner or a property.";

    private static readonly RecordBankAdjustment Valid = Contribution(UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId());

    private static readonly Dictionary<string, (RecordBankAdjustment Command, string[] Properties, string Message)> Cases = new()
    {
        ["OwnerId.Null"] = (Valid with { OwnerId = null }, ["OwnerId"], NoOwner),
        ["OwnerId.Empty"] = (Valid with { OwnerId = Guid.Empty }, ["OwnerId"], NoOwner),
        ["OwnerId.Upper"] = (Valid with { Kind = "OWNER-CONTRIBUTION", OwnerId = null }, ["OwnerId"], NoOwner),
        ["PropertyId.Null"] = (Valid with { PropertyId = null }, ["PropertyId"], NoProperty),
        ["PropertyId.Empty"] = (Valid with { PropertyId = Guid.Empty }, ["PropertyId"], NoProperty),
        ["ToBankAccountId"] = (Valid with { ToBankAccountId = UuidV7.NewId() }, ["ToBankAccountId"],
            "Only a transfer or a sweep names a destination bank account."),
        ["Fee.OwnerId"] = (Valid with { Kind = "fee", PropertyId = null }, ["OwnerId"], NotAnOwnerContribution),
        ["Fee.PropertyId"] = (Valid with { Kind = "contribution", OwnerId = null }, ["PropertyId"], NotAnOwnerContribution),
    };

    public static TheoryData<string> Refusals() => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_malformed_owner_contribution_is_refused_by_its_own_rule(string name)
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

    private async Task<PostResult> SendAsync(
        OrgScope scope, RecordBankAdjustment command, CancellationToken ct,
        IPropertyOwnership? ownership = null, TimeProvider? clock = null)
    {
        PostResult result = null!;
        await scope.RunAsync(async () => result = await AccountingSender
            .Create(scope, NoLeases, clock ?? Noon(new DateOnly(2026, 3, 1)), ownership: ownership ?? Owned)
            .Send(command, ct), ct);
        return result;
    }

    private static StoppedClock Noon(DateOnly day) => new(new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));

    private static Task AssertNothingPostedAsync(OrgScope scope, CancellationToken ct) =>
        scope.RunAsync(async () => (await scope.Db.Set<JournalEntry>().CountAsync(ct)).ShouldBe(0), ct);
}
