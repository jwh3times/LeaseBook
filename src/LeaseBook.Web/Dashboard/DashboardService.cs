using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.Dashboard;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Dashboard;

/// <summary>
/// Composes the dashboard payload (§C.6) in the <b>host</b> — the legitimate cross-module composition
/// root (P45 / ADR-007), so dispatching Accounting + Directory read queries via <see cref="ISender"/> and
/// merging in memory crosses no boundary (Reporting stays dormant; no cross-module SQL). The SPA does no
/// client-side financial math (TODO M2.4). Owner names are merged from the Directory lookup, the
/// <c>AggregateOwners</c> roll-up relabeled "All other owners" (P40) and excluded from
/// <c>ownersPayable</c> (P41). Uncleared KPIs are sourced live from the M4 bank register.
/// </summary>
public sealed class DashboardService(ISender sender, TimeProvider clock)
{
    public async Task<DashboardResponse> ComposeAsync(CancellationToken ct)
    {
        var ownerBalances = await sender.Query(new GetOwnerBalances(), ct);
        var ownerLookup = (await sender.Query(new GetOwnerLookup(), ct)).ToDictionary(o => o.Id);
        var bankBalances = await sender.Query(new GetBankBalances(), ct);
        var deposits = await sender.Query(new GetDepositRegister(), ct);
        var directoryKpis = await sender.Query(new GetDirectoryKpis(), ct);
        var now = clock.GetUtcNow();
        var collectedMtd = await sender.Query(new GetCollectedThisMonth(now.Year, now.Month), ct);

        bool IsSystem(Guid ownerId) => ownerLookup.TryGetValue(ownerId, out var o) && o.IsSystem;

        // trustTotal = Σ bank books (the M1 golden total, 483,620.69 on the demo); the banks list sums to it.
        var trustTotal = bankBalances.Rows.Sum(b => b.Book);

        // Uncleared KPIs — live from the M4 bank register (book − cleared sum; count of uncleared lines).
        var uncleared = bankBalances.Rows.Sum(b => b.Uncleared);
        var unclearedCount = bankBalances.Rows.Sum(b => b.UnclearedCount);

        // ownersPayable (P41) = Σ max(0, owner operating) over non-system owners — the disbursable amount,
        // negative-balance owners contribute 0. The prototype's 132,447.00 is authoring noise, not used.
        var ownersPayable = ownerBalances.Rows
            .Where(r => !IsSystem(r.OwnerId))
            .Sum(r => Math.Max(0m, r.Operating));

        // Hero: named rows, non-rollup first (by name), the system roll-up relabeled and last so totals tie.
        var heroRows = ownerBalances.Rows
            .Select(r =>
            {
                var isRollup = IsSystem(r.OwnerId);
                var name = isRollup ? "All other owners" : (ownerLookup.GetValueOrDefault(r.OwnerId)?.Name ?? "Unknown");
                return new OwnerBalanceHeroRow(r.OwnerId, name, r.Operating, r.Deposits, r.Total, isRollup);
            })
            .OrderBy(r => r.IsRollup).ThenBy(r => r.Name)
            .ToList();

        var heroTotals = new OwnerBalancesHeroTotals(
            heroRows.Sum(r => r.Operating), heroRows.Sum(r => r.Deposits), heroRows.Sum(r => r.Total));

        var bankRows = bankBalances.Rows
            .Select(b => new DashboardBankRow(b.BankAccountId, b.Name, b.Book, b.UnclearedCount)).ToList();

        var depositsAwaiting = deposits.Rows.Count(r => r.Kind == "deposit");
        var disbursementReady = ownerBalances.Rows.Count(r => !IsSystem(r.OwnerId) && r.Operating > 0m);

        // Honest, computed action items; each deep-links a route (some land on a later-milestone screen).
        var actionItems = new List<ActionItem>
        {
            new("deposits-awaiting", "info", "Deposits awaiting application",
                $"{depositsAwaiting} held deposit(s) — a liability until applied on move-out", "/banking"),
            new("reconciliation-due", unclearedCount == 0 ? "info" : "warn", "Bank reconciliation",
                unclearedCount == 0
                    ? "All bank items cleared — nothing to reconcile"
                    : $"{unclearedCount} uncleared item(s) across trust accounts", "/banking"),
            new("disbursement-ready", "alert", "Owner disbursement run ready",
                $"{disbursementReady} owner(s) with a positive operating balance this cycle", "/operations"),
        };

        return new DashboardResponse(
            new DashboardKpis(
                TrustTotal: trustTotal,
                OwnersPayable: ownersPayable,
                Uncleared: uncleared,
                UnclearedCount: unclearedCount,
                CollectedMtd: collectedMtd,
                CollectedTarget: directoryKpis.CollectedTarget,
                Vacancy: directoryKpis.Vacancy),
            new OwnerBalancesPanel(heroRows, heroTotals),
            new BanksPanel(bankRows),
            actionItems);
    }
}

public sealed record DashboardResponse(
    DashboardKpis Kpis, OwnerBalancesPanel OwnerBalances, BanksPanel Banks, IReadOnlyList<ActionItem> ActionItems);

public sealed record DashboardKpis(
    decimal TrustTotal, decimal OwnersPayable, decimal Uncleared, int UnclearedCount,
    decimal CollectedMtd, decimal CollectedTarget, int Vacancy);

public sealed record OwnerBalancesPanel(IReadOnlyList<OwnerBalanceHeroRow> Rows, OwnerBalancesHeroTotals Totals);

public sealed record OwnerBalanceHeroRow(
    Guid OwnerId, string Name, decimal Operating, decimal Deposits, decimal Total, bool IsRollup);

public sealed record OwnerBalancesHeroTotals(decimal Operating, decimal Deposits, decimal Total);

public sealed record BanksPanel(IReadOnlyList<DashboardBankRow> Rows);

public sealed record DashboardBankRow(Guid BankAccountId, string Name, decimal Book, int UnclearedCount);

public sealed record ActionItem(string Id, string Kind, string Title, string Detail, string Route);
