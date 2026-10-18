using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The owner statement read (§4.1) sent through the real dispatcher: the rules that refuse a
/// malformed request, the sections in render order with their titles, the basis that decides which
/// movement a section holds, and the tie-out flag that says a deposit or credit was recognised on
/// application. The management fee's PM-income credit never appears; only the owner's debit does.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OwnerStatementQueryTests(PostgresFixture fixture)
{
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _tenant = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();

    // ---- validation: each rule refuses its own field, and nothing else does ----

    private static readonly Guid Some = UuidV7.NewId();
    private static readonly Guid Other = UuidV7.NewId();
    private static readonly GetOwnerStatementData Valid = new([Some], null, 2026, 2, "cash");

    private static readonly Dictionary<string, (GetOwnerStatementData Query, string Property, string? Message)> Cases = new()
    {
        ["OwnerIds"] = (Valid with { OwnerIds = [] }, "OwnerIds", null),
        ["Year"] = (Valid with { Year = 1999 }, "Year", null),
        ["Month"] = (Valid with { Month = 0 }, "Month", null),
        ["Basis"] = (Valid with { Basis = "both" }, "Basis", "Basis must be 'cash' or 'accrual'."),
        ["Anchors.Owner"] = (
            Valid with { Anchors = new Dictionary<Guid, StatementAnchor> { [Other] = new(0m, DateTime.UtcNow) } },
            "Anchors", "Every statement anchor must belong to a requested owner."),
        ["Anchors.Utc"] = (
            Valid with { Anchors = new Dictionary<Guid, StatementAnchor> { [Some] = new(0m, DateTime.Now) } },
            "Anchors", "Statement anchor instants must be UTC."),
    };

    public static TheoryData<string> Refusals() => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_malformed_statement_request_is_refused_by_its_own_rule_before_the_handler_runs(string name)
    {
        var (query, property, message) = Cases[name];
        var sender = await UnusableSenderAsync();

        var refused = await Should.ThrowAsync<ValidationException>(() => sender.Query(query));

        refused.Errors.Select(e => e.PropertyName).ShouldBe([property]);
        if (message is not null)
        {
            refused.Errors.Single().ErrorMessage.ShouldBe(message);
        }
    }

    // ---- sections, basis and the tie-out ----

    [Fact]
    public async Task A_statement_lists_its_sections_in_render_order_and_flags_a_credit_recognised_on_application()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);
        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            var bank = scope.TrustBankId;
            // Before February: Beginning.
            await events.PostAsync(new OwnerContribution(_owner, _property, new Money(1000m), new DateOnly(2026, 1, 15), bank, "reserve"), ct);
            // February, posted in an order the sections do not follow.
            await events.PostAsync(new RentCharged(_tenant, _property, _owner, null, new Money(1450m), Feb(1), "February rent"), ct);
            await events.PostAsync(new CreditIssued(_tenant, _property, _owner, new Money(50m), Feb(2), "goodwill"), ct);
            await events.PostAsync(new PaymentReceived(
                _tenant, _property, _owner, new Money(1400m), Feb(3), PaymentMethod.Ach, bank, "February payment"), ct);
            await events.PostAsync(new OwnerContribution(_owner, _property, new Money(2000m), Feb(10), bank, "roof fund"), ct);
            await events.PostAsync(new ManagementFeeAssessed(_owner, _property, new Money(145m), Feb(27), bank, "management fee"), ct);
            await events.PostAsync(new OwnerDisbursed(_owner, new Money(3000m), Feb(28), bank, "owner draw"), ct);
        }, ct);

        // Accrual: the rent charge is income and the credit is applied; the receipt moves no accrual equity.
        var accrual = await StatementAsync(scope, "accrual", ct);
        accrual.Sections.Select(s => (s.Key, s.Title, s.Subtotal)).ShouldBe(
        [
            (StatementSectionKey.Income, "Income — rent collected", 1450m),
            (StatementSectionKey.OperatingExpenses, "Operating expenses", -145m),
            (StatementSectionKey.AppliedDepositsCredits, "Applied deposits & credits", -50m),
            (StatementSectionKey.Contributions, "Owner contributions", 2000m),
            (StatementSectionKey.Disbursement, "Owner disbursement", -3000m),
        ]);
        accrual.Sections.Single(s => s.Key == StatementSectionKey.Income).Lines.Single().EventType.ShouldBe("RentCharged");
        (accrual.Beginning, accrual.Ending).ShouldBe((1000m, 1255m));
        accrual.TieOut.ShouldBe(new StatementTieOut(
            Balanced: true, Variance: 0m, PmIncomeExcluded: true, DepositsRecognizedOnApplication: true));

        // Cash: the receipt is the income, and the accrual-only credit is not on the statement at all.
        var cash = await StatementAsync(scope, "cash", ct);
        cash.Sections.Select(s => (s.Key, s.Subtotal)).ShouldBe(
        [
            (StatementSectionKey.Income, 1400m),
            (StatementSectionKey.OperatingExpenses, -145m),
            (StatementSectionKey.Contributions, 2000m),
            (StatementSectionKey.Disbursement, -3000m),
        ]);
        cash.Sections.Single(s => s.Key == StatementSectionKey.Income).Lines.Single().EventType.ShouldBe("PaymentReceived");
        (cash.Beginning, cash.Ending).ShouldBe((1000m, 1255m));
        cash.TieOut.ShouldBe(new StatementTieOut(
            Balanced: true, Variance: 0m, PmIncomeExcluded: true, DepositsRecognizedOnApplication: false));

        // The 145 the PM earned is the owner's expense on both bases, never a credit to the owner.
        foreach (var statement in new[] { accrual, cash })
        {
            statement.Sections.SelectMany(s => s.Lines).Where(l => l.EventType == "ManagementFeeAssessed")
                .Select(l => l.Amount).ShouldBe([-145m]);
            statement.Adjustments.ShouldBeNull();
        }
    }

    // ---- helpers ----

    private static readonly Leases NoLeases = new(new Dictionary<Guid, TenantPostingDimensions>());

    private static DateOnly Feb(int day) => new(2026, 2, day);

    private async Task<OwnerStatement> StatementAsync(OrgScope scope, string basis, CancellationToken ct)
    {
        OwnerStatementDataResponse result = null!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, NoLeases)
            .Query(new GetOwnerStatementData([_owner], null, 2026, 2, basis), ct), ct);
        return result.ByOwner.ShouldHaveSingleItem().Value;
    }

    /// <summary>A context already disposed: a request that got past its validator would fail on its first read.</summary>
    private async Task<ISender> UnusableSenderAsync()
    {
        AppDbContext db = fixture.CreateContext(fixture.AppConnectionString, new OrgContext(), new ActorContext());
        await db.DisposeAsync();
        return AccountingSender.Create(db, new OrgContext(), new ActorContext(), NoLeases);
    }
}
