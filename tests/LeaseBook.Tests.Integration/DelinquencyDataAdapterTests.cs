using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.Reporting;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Web.Adapters;
using Shouldly;
using DirectoryLeaseScheduleRow = LeaseBook.Modules.Directory.Features.Reporting.LeaseScheduleRow;

namespace LeaseBook.Tests.Integration;

public sealed class DelinquencyDataAdapterTests
{
    [Fact]
    public async Task One_active_lease_carries_its_specific_rent_obligation()
    {
        var tenantId = Guid.NewGuid();
        var lease = Lease(tenantId, "1A");
        var rentEntryId = Guid.NewGuid();
        var rentRef = $"rent:2026-03:lease={lease.LeaseId}";
        var adapter = new DelinquencyDataAdapter(new StubSender(
            Aging(tenantId, oldestAgeDays: 17),
            new LeaseScheduleResponse([lease]),
            new RentObligationEntriesResponse([new(rentEntryId, rentRef, tenantId)])));

        var rows = await adapter.GetAsync(
            year: 2026,
            month: 3,
            asOf: new DateOnly(2026, 3, 31),
            TestContext.Current.CancellationToken);

        var row = rows.ShouldHaveSingleItem();
        row.LeaseId.ShouldBe(lease.LeaseId);
        row.Balance.ShouldBe(1_000m);
        row.Attribution
            .ShouldBeOfType<DelinquencyAttribution.AttributedToLease>()
            .RentObligationEntryId.ShouldBe(rentEntryId);
    }

    [Fact]
    public async Task Multiple_active_leases_carry_explicit_ambiguity_for_every_lease()
    {
        var tenantId = Guid.NewGuid();
        var firstLease = Lease(tenantId, "1A");
        var secondLease = Lease(tenantId, "1B");
        var adapter = new DelinquencyDataAdapter(new StubSender(
            Aging(tenantId, oldestAgeDays: 17),
            new LeaseScheduleResponse([firstLease, secondLease]),
            new RentObligationEntriesResponse([])));

        var rows = await adapter.GetAsync(
            year: 2026,
            month: 3,
            asOf: new DateOnly(2026, 3, 31),
            TestContext.Current.CancellationToken);

        rows.Count.ShouldBe(2);
        rows.ShouldContain(row => row.LeaseId == firstLease.LeaseId);
        rows.ShouldContain(row => row.LeaseId == secondLease.LeaseId);
        rows.ShouldAllBe(row =>
            row.Attribution is DelinquencyAttribution.AmbiguousMultipleActiveLeases);
    }

    private static DelinquencyResponse Aging(Guid tenantId, int oldestAgeDays) =>
        new([
            new DelinquencyRow(
                TenantId: tenantId,
                Current: 0m,
                D1_30: 1_000m,
                D31_60: 0m,
                D61_90: 0m,
                Over90: 0m,
                Total: 1_000m,
                UnappliedCredit: 0m,
                OldestAgeDays: oldestAgeDays),
        ]);

    private static DirectoryLeaseScheduleRow Lease(Guid tenantId, string unitLabel) =>
        new(
            LeaseId: Guid.NewGuid(),
            TenantId: tenantId,
            PropertyId: Guid.NewGuid(),
            OwnerId: Guid.NewGuid(),
            UnitId: Guid.NewGuid(),
            TenantName: "Ada Tenant",
            UnitLabel: unitLabel,
            Rent: 1_000m,
            RentDueDay: 1,
            StartDate: new DateOnly(2025, 1, 1),
            EndDate: new DateOnly(2027, 12, 31));

    private sealed class StubSender(
        DelinquencyResponse aging,
        LeaseScheduleResponse schedule,
        RentObligationEntriesResponse rentObligations) : ISender
    {
        public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default) =>
            throw new InvalidOperationException($"Unexpected command {command.GetType().Name}.");

        public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default)
        {
            object result = query switch
            {
                GetDelinquencyAging => aging,
                GetActiveLeaseSchedule => schedule,
                GetRentObligationEntries => rentObligations,
                _ => throw new InvalidOperationException($"Unexpected query {query.GetType().Name}."),
            };

            return Task.FromResult((TResult)result);
        }
    }
}
