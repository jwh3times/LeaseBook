using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-04: voids are linked reversal entries posted through the engine — never mutations — landing in an
/// open period, reversible at most once, and never reversible themselves.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ReversalServiceTests(PostgresFixture fixture)
{
    private readonly Guid _ownerId = UuidV7.NewId();
    private readonly Guid _propertyId = UuidV7.NewId();
    private readonly Guid _tenantId = UuidV7.NewId();

    [Fact]
    public async Task Reversing_creates_a_linked_dated_mirror_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(fixture, ct);

        Guid originalId = default;
        Guid reversalId = default;
        await scope.RunAsync(async () =>
        {
            originalId = await AccountingTestHarness.Posting(scope).PostAsync(Rent("r1"), ct);
            reversalId = await AccountingTestHarness.Reversal(scope)
                .ReverseAsync(originalId, "entered in error", new DateOnly(2026, 3, 1), ct);
        }, ct);

        JournalEntry? reversal = null;
        await scope.RunAsync(async () =>
            reversal = await scope.Db.Set<JournalEntry>().AsNoTracking()
                .Include(e => e.Lines).SingleAsync(e => e.Id == reversalId, ct), ct);

        reversal!.ReversesEntryId.ShouldBe(originalId);
        reversal.EventType.ShouldBe(EventTypes.EntryVoided);
        reversal.EntryDate.ShouldBe(new DateOnly(2026, 3, 1)); // open period, not the original's month
        reversal.Description.ShouldBe("VOID: entered in error");

        // Original was DR receivable / CR owner_equity; the mirror swaps both sides.
        reversal.Lines.Single(l => l.AccountClass == AccountClass.TenantReceivable).Credit!.Value.Amount.ShouldBe(1450m);
        reversal.Lines.Single(l => l.AccountClass == AccountClass.OwnerEquity).Debit!.Value.Amount.ShouldBe(1450m);
    }

    [Fact]
    public async Task Reversing_the_same_entry_twice_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(fixture, ct);

        await scope.RunAsync(async () =>
        {
            var id = await AccountingTestHarness.Posting(scope).PostAsync(Rent("r2"), ct);
            await AccountingTestHarness.Reversal(scope).ReverseAsync(id, "first", new DateOnly(2026, 3, 1), ct);

            var ex = await Should.ThrowAsync<AlreadyReversedException>(() =>
                AccountingTestHarness.Reversal(scope).ReverseAsync(id, "second", new DateOnly(2026, 3, 1), ct));
            ex.Code.ShouldBe("already_reversed");
        }, ct);
    }

    [Fact]
    public async Task A_reversal_cannot_itself_be_reversed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(fixture, ct);

        await scope.RunAsync(async () =>
        {
            var id = await AccountingTestHarness.Posting(scope).PostAsync(Rent("r3"), ct);
            var reversalId = await AccountingTestHarness.Reversal(scope).ReverseAsync(id, "void", new DateOnly(2026, 3, 1), ct);

            await Should.ThrowAsync<AlreadyReversedException>(() =>
                AccountingTestHarness.Reversal(scope).ReverseAsync(reversalId, "void the void", new DateOnly(2026, 3, 1), ct));
        }, ct);
    }

    [Fact]
    public async Task A_reversal_dated_into_a_closed_period_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(fixture, ct);

        await scope.RunAsync(async () =>
        {
            var id = await AccountingTestHarness.Posting(scope).PostAsync(Rent("r4"), ct);
            await AccountingTestHarness.Periods(scope).CloseAsync(2026, 3, ct);

            await Should.ThrowAsync<PeriodClosedException>(() =>
                AccountingTestHarness.Reversal(scope).ReverseAsync(id, "void", new DateOnly(2026, 3, 15), ct));
        }, ct);
    }

    private PostEntryRequest Rent(string? sourceRef) => new(
        new DateOnly(2026, 2, 1), "RentCharged", null, "Feb rent", sourceRef,
        [
            new PostLineRequest(AccountCodes.TenantReceivable, new Money(1450m), null, EntryBasis.Accrual,
                PropertyId: _propertyId, OwnerId: _ownerId, TenantId: _tenantId),
            new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(1450m), EntryBasis.Accrual,
                PropertyId: _propertyId, OwnerId: _ownerId),
        ]);
}
