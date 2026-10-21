using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
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
/// The register's own commands (P62/P65/P68), sent through the real dispatcher: a bank adjustment's
/// validator and the event and default text each kind posts, and the guards on clearing — an empty
/// batch, a batch with a line that is not a bank line, and a missing organization context. The
/// templates' lines are pinned in <see cref="BankAdjustmentTemplatesTests"/>; clearing a payout as
/// one is pinned in <see cref="PostPaymentSettlementTests"/>.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BankingCommandTests(PostgresFixture fixture)
{
    private static readonly DateOnly Feb5 = new(2026, 2, 5);

    // ---- validation: each rule refuses its own field, and nothing else does ----

    private static readonly Guid Some = UuidV7.NewId();
    private static readonly Guid Other = UuidV7.NewId();
    private const string AmountMessage = "Amount must be a positive value with at most 2 decimal places.";
    private const string NoDestination = "A transfer requires a destination bank account.";
    private const string NotATransfer = "Only a transfer names a destination bank account.";

    private static readonly RecordBankAdjustment Fee = new("fee", 25m, Feb5, Some, null, null, "adj:1");
    private static readonly RecordBankAdjustment Transfer = new("transfer", 25m, Feb5, Some, Other, null, "adj:1");

    private static readonly Dictionary<string, (RecordBankAdjustment Command, string[] Properties, string? Message)> Cases = new()
    {
        ["BankAccountId"] = (Fee with { BankAccountId = Guid.Empty }, ["BankAccountId"], null),
        ["SourceRef"] = (Fee with { SourceRef = "" }, ["SourceRef"], null),
        ["Kind"] = (Fee with { Kind = "wire" }, ["Kind"], "Kind must be one of: fee, interest, transfer."),
        ["Amount"] = (Fee with { Amount = 0m }, ["Amount"], AmountMessage),
        ["Transfer.ToBankAccountId.Null"] = (Transfer with { ToBankAccountId = null }, ["ToBankAccountId"], NoDestination),
        ["Transfer.ToBankAccountId.Upper"] = (
            Transfer with { Kind = "TRANSFER", ToBankAccountId = null }, ["ToBankAccountId"], NoDestination),
        ["Transfer.ToBankAccountId.Empty"] = (Transfer with { ToBankAccountId = Guid.Empty }, ["ToBankAccountId"], NoDestination),
        ["Transfer.ToBankAccountId.Same"] = (Transfer with { ToBankAccountId = Some }, ["ToBankAccountId"],
            "A transfer's source and destination must differ."),
        // The handler would drop a destination on any other kind, so the validator refuses it.
        ["Fee.ToBankAccountId"] = (Fee with { ToBankAccountId = Other }, ["ToBankAccountId"], NotATransfer),
        ["Interest.ToBankAccountId"] = (
            Fee with { Kind = "Interest", ToBankAccountId = Other }, ["ToBankAccountId"], NotATransfer),
        ["Fee.ToBankAccountId.Empty"] = (Fee with { ToBankAccountId = Guid.Empty }, ["ToBankAccountId"], NotATransfer),
    };

    public static TheoryData<string> Refusals() => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_malformed_adjustment_is_refused_by_its_own_rule_before_the_handler_runs(string name)
    {
        var (command, properties, message) = Cases[name];
        var sender = await UnusableSenderAsync();

        var refused = await Should.ThrowAsync<ValidationException>(() => sender.Send(command));

        refused.Errors.Select(e => e.PropertyName).ShouldBe(properties);
        if (message is not null)
        {
            refused.Errors.First().ErrorMessage.ShouldBe(message);
        }
    }

    [Fact]
    public async Task A_kind_the_handler_does_not_know_is_refused_by_it_too()
    {
        // Unreachable past the validator; the handler still refuses rather than posting nothing.
        var refused = await Should.ThrowAsync<ValidationException>(() =>
            new RecordBankAdjustmentHandler(new NoEvents()).Handle(Fee with { Kind = "wire" }, TestContext.Current.CancellationToken));

        refused.Message.ShouldBe("Unknown bank-adjustment kind.");
    }

    // ---- what each kind posts ----

    public static TheoryData<string, string, string> Kinds() => new()
    {
        { "fee", "BankFeeCharged", "Bank fee" },
        { "Interest", "InterestEarned", "Interest" },
        { "transfer", "TrustTransfer", "Transfer" },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Each_kind_posts_its_own_event_with_a_default_description_and_keeps_one_it_is_given(
        string kind, string eventType, string description)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        // Held PM fees in the trust bank for a fee or a transfer to draw on.
        await scope.RunAsync(() => Events(scope).PostAsync(
            new InterestEarned(new Money(500m), Feb5, scope.TrustBankId, "seed", "seed:1"), ct), ct);

        // Only a transfer names a destination; the validator refuses one on the other kinds.
        Guid? to = eventType == "TrustTransfer" ? scope.OperatingBankId : null;
        var plain = await SendAsync(scope, new RecordBankAdjustment(
            kind, 25m, Feb5, scope.TrustBankId, to, null, $"adj:{kind}", "staff only"), ct);
        var named = await SendAsync(scope, new RecordBankAdjustment(
            kind, 10m, Feb5, scope.TrustBankId, to, "Statement line text", $"adj:{kind}:named"), ct);

        var entry = await EntryAsync(scope, plain.EntryId, ct);
        (entry.EventType, entry.Description, entry.SourceRef, entry.InternalNote, entry.EntryDate)
            .ShouldBe((eventType, description, $"adj:{kind}", "staff only", Feb5));
        (await EntryAsync(scope, named.EntryId, ct)).Description.ShouldBe("Statement line text");

        var trust = AccountCodes.TrustBank(scope.TrustBankId);
        var operating = AccountCodes.PmOperatingBank(scope.OperatingBankId);
        (await ReadLinesAsync(scope, plain.EntryId, ct)).ShouldBe(eventType switch
        {
            "BankFeeCharged" =>
            [
                new(AccountCodes.PmIncome, 25m, null, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
                new(trust, null, 25m, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
            ],
            "InterestEarned" =>
            [
                new(trust, 25m, null, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
                new(AccountCodes.PmIncome, null, 25m, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
            ],
            _ =>
            [
                new(operating, 25m, null, EntryBasis.Both, null, null, null, null, scope.OperatingBankId),
                new(trust, null, 25m, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
                new(AccountCodes.PmIncome, 25m, null, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
                new(AccountCodes.PmIncome, null, 25m, EntryBasis.Both, null, null, null, null, scope.OperatingBankId),
            ],
        }, ignoreOrder: true);
    }

    // ---- the guards on clearing ----

    [Fact]
    public async Task An_empty_batch_is_answered_without_reading_anything_or_needing_an_organization()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = await UnusableSenderAsync();

        (await sender.Send(new ApplyClearances([]), ct)).ShouldBe(new ClearancesResult(0));
        (await sender.Query(new GetPayoutReferences([]), ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Clearing_is_refused_without_an_organization_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(() =>
            new ApplyClearancesHandler(scope.Db, new OrgContext()).Handle(new ApplyClearances([UuidV7.NewId()]), ct), ct));

        refused.Message.ShouldBe("ApplyClearances requires an ambient organization context.");
    }

    [Fact]
    public async Task A_batch_naming_a_line_that_is_not_a_bank_line_clears_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        Guid entry = default;
        await scope.RunAsync(async () => entry = await Events(scope).PostAsync(
            new InterestEarned(new Money(40m), Feb5, scope.TrustBankId, "interest", "int:1"), ct), ct);
        List<(Guid Id, AccountClass Class)> lines = [];
        await scope.RunAsync(async () => lines = (await scope.Db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.EntryId == entry).Select(l => new { l.Id, l.AccountClass }).ToListAsync(ct))
            .Select(l => (l.Id, l.AccountClass)).ToList(), ct);
        var bank = lines.Single(l => l.Class == AccountClass.TrustBank).Id;
        var income = lines.Single(l => l.Class != AccountClass.TrustBank).Id;

        var refused = await Should.ThrowAsync<ValidationException>(() => scope.RunAsync(() =>
            AccountingSender.Create(scope, NoLeases).Send(new ApplyClearances([bank, income]), ct), ct));

        refused.Message.ShouldBe("Every id to clear must be a bank-account journal line in this org.");
        await scope.RunAsync(async () => (await scope.Db.Set<BankLineState>().CountAsync(ct)).ShouldBe(0), ct);
    }

    // ---- helpers ----

    private static readonly Leases NoLeases = new(new Dictionary<Guid, TenantPostingDimensions>());

    /// <summary>Posts nothing: a handler that reached it would have chosen an event.</summary>
    private sealed class NoEvents : IAccountingEvents
    {
        public Task<Guid> PostAsync(AccountingEvent businessEvent, CancellationToken ct) =>
            throw new InvalidOperationException($"Nothing should post, but {businessEvent.GetType().Name} did.");
    }

    /// <summary>A context already disposed: a request that got past its validator would fail on its first read.</summary>
    private async Task<ISender> UnusableSenderAsync()
    {
        AppDbContext db = fixture.CreateContext(fixture.AppConnectionString, new OrgContext(), new ActorContext());
        await db.DisposeAsync();
        return AccountingSender.Create(db, new OrgContext(), new ActorContext(), NoLeases);
    }

    private static async Task<T> SendAsync<T>(OrgScope scope, ICommand<T> command, CancellationToken ct)
    {
        T result = default!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, NoLeases).Send(command, ct), ct);
        return result;
    }

    private static async Task<JournalEntry> EntryAsync(OrgScope scope, Guid id, CancellationToken ct)
    {
        JournalEntry entry = null!;
        await scope.RunAsync(async () => entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == id, ct), ct);
        return entry;
    }
}
