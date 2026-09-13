using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// ADR-045: a statement carries forward from the one issued for the preceding period. The identity
/// under test is <c>IssuedEnding + Σ Lines + Unitemized = Beginning</c> — the issued figure is
/// authoritative, the lines explain the difference, and anything they cannot explain is surfaced
/// rather than absorbed.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class StatementCarryForwardTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_backdated_posting_after_issue_is_itemized_and_chains_to_the_live_beginning()
    {
        var ct = TestContext.Current.CancellationToken;
        var (scope, owner, tenant, property) = await ArrangeAsync(ct);
        await using var _ = scope;

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(Rent(tenant, property, owner, 100.00m, new DateOnly(2026, 5, 10)), ct);

            // Issue May: its ending balance and read instant are what the owner was given.
            var may = await ReadAsync(scope, owner, 2026, 5, anchors: null, ct);
            may.Ending.ShouldBe(100.00m);

            // Keyed in after May was issued, dated into May — the case nothing used to reflect.
            var late = await events.PostAsync(Rent(tenant, property, owner, 40.00m, new DateOnly(2026, 5, 20)), ct);

            var june = await ReadAsync(scope, owner, 2026, 6, Anchor(owner, may), ct);

            var adj = june.Adjustments.ShouldNotBeNull();
            adj.IssuedEnding.ShouldBe(100.00m);
            var line = adj.Lines.ShouldHaveSingleItem();
            line.EntryId.ShouldBe(late);
            line.Date.ShouldBe(new DateOnly(2026, 5, 20));
            line.Amount.ShouldBe(40.00m);
            line.PostedAt.ShouldBeGreaterThan(may.AsOf, "an adjustment is by definition posted after the anchor read");
            adj.Unitemized.ShouldBe(0m);
            adj.Total.ShouldBe(40.00m);

            (adj.IssuedEnding + adj.Total).ShouldBe(june.Beginning, "the owner's two documents chain exactly");
            june.Beginning.ShouldBe(140.00m);
            june.TieOut.Balanced.ShouldBeTrue("carry-forward is a decomposition of Beginning, never a new figure");
        }, ct);
    }

    [Fact]
    public async Task Nothing_posted_since_issue_yields_a_zero_carry_forward()
    {
        var ct = TestContext.Current.CancellationToken;
        var (scope, owner, tenant, property) = await ArrangeAsync(ct);
        await using var _ = scope;

        await scope.RunAsync(async () =>
        {
            await Events(scope).PostAsync(Rent(tenant, property, owner, 100.00m, new DateOnly(2026, 5, 10)), ct);
            var may = await ReadAsync(scope, owner, 2026, 5, anchors: null, ct);

            var june = await ReadAsync(scope, owner, 2026, 6, Anchor(owner, may), ct);

            var adj = june.Adjustments.ShouldNotBeNull();
            adj.Lines.ShouldBeEmpty();
            adj.Total.ShouldBe(0m);
            adj.Unitemized.ShouldBe(0m);
        }, ct);
    }

    [Fact]
    public async Task Without_an_anchor_there_is_no_carry_forward()
    {
        var ct = TestContext.Current.CancellationToken;
        var (scope, owner, tenant, property) = await ArrangeAsync(ct);
        await using var _ = scope;

        await scope.RunAsync(async () =>
        {
            await Events(scope).PostAsync(Rent(tenant, property, owner, 100.00m, new DateOnly(2026, 5, 10)), ct);

            var june = await ReadAsync(scope, owner, 2026, 6, anchors: null, ct);

            june.Adjustments.ShouldBeNull();
            june.Beginning.ShouldBe(100.00m);
        }, ct);
    }

    [Fact]
    public async Task A_posting_dated_into_the_new_period_is_activity_not_an_adjustment()
    {
        var ct = TestContext.Current.CancellationToken;
        var (scope, owner, tenant, property) = await ArrangeAsync(ct);
        await using var _ = scope;

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(Rent(tenant, property, owner, 100.00m, new DateOnly(2026, 5, 10)), ct);
            var may = await ReadAsync(scope, owner, 2026, 5, anchors: null, ct);

            // Posted after the anchor read, but dated into June: ordinary June activity.
            await events.PostAsync(Rent(tenant, property, owner, 60.00m, new DateOnly(2026, 6, 3)), ct);

            var june = await ReadAsync(scope, owner, 2026, 6, Anchor(owner, may), ct);

            june.Adjustments.ShouldNotBeNull().Lines.ShouldBeEmpty();
            june.Adjustments!.Total.ShouldBe(0m);
            june.Sections.Single(s => s.Key == StatementSectionKey.Income).Subtotal.ShouldBe(60.00m);
        }, ct);
    }

    /// <summary>
    /// An opening position dated into the new period belongs to Beginning but can never have been part of
    /// the prior period's issued ending — even when it was posted before that statement was issued. It is
    /// an itemized line, not an unexplained remainder.
    /// </summary>
    [Fact]
    public async Task An_in_period_opening_position_posted_before_issue_is_itemized_not_unitemized()
    {
        var ct = TestContext.Current.CancellationToken;
        var (scope, owner, tenant, property) = await ArrangeAsync(ct);
        await using var _ = scope;

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(Rent(tenant, property, owner, 100.00m, new DateOnly(2026, 5, 10)), ct);
            await events.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.OwnerEquity, Debit: null, Credit: new Money(250.00m), EntryBasis.Both,
                Cutover: new DateOnly(2026, 6, 15), SourceRef: $"open:{owner}", OwnerId: owner), ct);

            // Issued after the opening position was posted; May's ending cannot include a June-dated entry.
            var may = await ReadAsync(scope, owner, 2026, 5, anchors: null, ct);
            may.Ending.ShouldBe(100.00m);

            var june = await ReadAsync(scope, owner, 2026, 6, Anchor(owner, may), ct);

            june.Beginning.ShouldBe(350.00m);
            var adj = june.Adjustments.ShouldNotBeNull();
            adj.Lines.ShouldHaveSingleItem().Amount.ShouldBe(250.00m);
            adj.Unitemized.ShouldBe(0m, "an opening position is explained by date, not by posting time");
            (adj.IssuedEnding + adj.Total).ShouldBe(june.Beginning);
        }, ct);
    }

    /// <summary>
    /// The issued figure is authoritative even when no posting explains the whole difference — the
    /// shape the straddling-commit window produces. The remainder is its own value, so a renderer can
    /// label it; it is never folded into a line or dropped.
    /// </summary>
    [Fact]
    public async Task A_difference_no_posted_after_entry_explains_is_surfaced_as_unitemized()
    {
        var ct = TestContext.Current.CancellationToken;
        var (scope, owner, tenant, property) = await ArrangeAsync(ct);
        await using var _ = scope;

        await scope.RunAsync(async () =>
        {
            await Events(scope).PostAsync(Rent(tenant, property, owner, 100.00m, new DateOnly(2026, 5, 10)), ct);

            // An anchor read after every posting, but recording 25.00 less than the journal holds.
            var anchors = new Dictionary<Guid, StatementAnchor>
            {
                [owner] = new(IssuedEnding: 75.00m, AsOf: DateTime.UtcNow),
            };

            var june = await ReadAsync(scope, owner, 2026, 6, anchors, ct);

            var adj = june.Adjustments.ShouldNotBeNull();
            adj.Lines.ShouldBeEmpty();
            adj.Total.ShouldBe(25.00m);
            adj.Unitemized.ShouldBe(25.00m);
            (adj.IssuedEnding + adj.Lines.Sum(l => l.Amount) + adj.Unitemized).ShouldBe(june.Beginning);
        }, ct);
    }

    [Fact]
    public async Task Anchors_are_per_owner_and_never_leak_across_owners()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = UuidV7.NewId();
        var other = UuidV7.NewId();
        var tenant = UuidV7.NewId();
        var property = UuidV7.NewId();
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner, other], tenants: [tenant], properties: [property]);

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(Rent(tenant, property, owner, 100.00m, new DateOnly(2026, 5, 10)), ct);
            await events.PostAsync(Rent(tenant, property, other, 30.00m, new DateOnly(2026, 5, 10)), ct);
            var may = await ReadAsync(scope, owner, 2026, 5, anchors: null, ct);

            await events.PostAsync(Rent(tenant, property, other, 70.00m, new DateOnly(2026, 5, 20)), ct);

            var result = await new GetOwnerStatementDataHandler(scope.Db).Handle(
                new GetOwnerStatementData([owner, other], null, 2026, 6, "accrual", Anchor(owner, may)), ct);

            result.ByOwner[owner].Adjustments.ShouldNotBeNull().Lines.ShouldBeEmpty(
                "the other owner's backdated posting is not this owner's adjustment");
            result.ByOwner[other].Adjustments.ShouldBeNull("an owner without an issued statement has no anchor");
        }, ct);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private async Task<(OrgScope Scope, Guid Owner, Guid Tenant, Guid Property)> ArrangeAsync(CancellationToken ct)
    {
        var owner = UuidV7.NewId();
        var tenant = UuidV7.NewId();
        var property = UuidV7.NewId();
        var scope = await ProvisionedScopeAsync(fixture, ct);
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner], tenants: [tenant], properties: [property]);
        return (scope, owner, tenant, property);
    }

    private static RentCharged Rent(Guid tenant, Guid property, Guid owner, decimal amount, DateOnly date) =>
        new(tenant, property, owner, null, new Money(amount), date, "rent");

    private static Dictionary<Guid, StatementAnchor> Anchor(Guid owner, OwnerStatement issued) =>
        new() { [owner] = new StatementAnchor(issued.Ending, issued.AsOf) };

    private static async Task<OwnerStatement> ReadAsync(
        OrgScope scope, Guid owner, int year, int month,
        IReadOnlyDictionary<Guid, StatementAnchor>? anchors, CancellationToken ct)
    {
        var result = await new GetOwnerStatementDataHandler(scope.Db).Handle(
            new GetOwnerStatementData([owner], null, year, month, "accrual", anchors), ct);
        return result.ByOwner[owner];
    }
}
