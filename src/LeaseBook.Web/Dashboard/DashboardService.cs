using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.Dashboard;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Operations.Features.Dashboard;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Dashboard;

/// <summary>
/// Composes the dashboard payload (§C.6) in the <b>host</b> — the legitimate cross-module composition
/// root (P45 / ADR-007), so dispatching Accounting + Directory read queries via <see cref="ISender"/> and
/// merging in memory crosses no boundary (no cross-module SQL). The SPA does no client-side financial
/// math — every figure here is server-computed. Owner names are merged from the Directory lookup, the
/// <c>AggregateOwners</c> roll-up relabeled "All other owners" (P40). Fiduciary bank KPIs are scoped
/// to trust-class accounts, tenant receipts are event-attributed by Accounting, and the amount
/// available to disburse comes from Operations' canonical fee-then-reserve policy.
/// </summary>
public sealed class DashboardService(
    ISender sender,
    TimeProvider clock,
    DashboardMetricsService operationsMetrics)
{
    public async Task<DashboardResponse> ComposeAsync(CancellationToken ct)
    {
        // Explicit "cash": the dashboard's owner ending balances are distributable cash (#230).
        var ownerBalances = await sender.Query(new GetOwnerBalances("cash"), ct);
        var ownerLookup = (await sender.Query(new GetOwnerLookup(), ct)).ToDictionary(o => o.Id);
        var bankBalances = await sender.Query(new GetBankBalances(), ct);
        var deposits = await sender.Query(new GetDepositRegister(), ct);
        var directoryKpis = await sender.Query(new GetDirectoryKpis(), ct);
        var now = clock.GetUtcNow();
        var tenantPaymentsMtd = await sender.Query(new GetTenantPaymentsReceived(now.Year, now.Month), ct);
        var operational = await operationsMetrics.GetAsync(now.Year, now.Month, ct);

        bool IsSystem(Guid ownerId) => ownerLookup.TryGetValue(ownerId, out var o) && o.IsSystem;

        var trustBanks = bankBalances.Rows.Where(bank => bank.IsTrust).ToList();

        // Fiduciary cash only. PM operating cash remains visible on Accounting's bank-balance read,
        // but is not part of a trust position and does not belong in this panel or its totals.
        var trustTotal = trustBanks.Sum(b => b.Book);

        // Uncleared KPIs — live from the M4 bank register (book − cleared sum; count of uncleared lines).
        var uncleared = trustBanks.Sum(b => b.Uncleared);
        var unclearedCount = trustBanks.Sum(b => b.UnclearedCount);

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

        var bankRows = trustBanks
            .Select(b => new DashboardBankRow(b.BankAccountId, b.Name, b.Book, b.UnclearedCount)).ToList();

        var depositsAwaiting = deposits.Rows.Count(r => r.Kind == "deposit");

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
                $"{operational.OwnersAvailableToDisburse} owner(s) above fees and reserve floors", "/operations"),
        };

        return new DashboardResponse(
            new DashboardKpis(
                TrustTotal: trustTotal,
                AvailableToDisburse: operational.AvailableToDisburse,
                Uncleared: uncleared,
                UnclearedCount: unclearedCount,
                TenantPaymentsMtd: tenantPaymentsMtd,
                ScheduledRent: operational.ScheduledRent,
                Vacancy: directoryKpis.Vacancy),
            new OwnerBalancesPanel(heroRows, heroTotals),
            new BanksPanel(bankRows),
            actionItems);
    }
}

public sealed record DashboardResponse(
    DashboardKpis Kpis, OwnerBalancesPanel OwnerBalances, BanksPanel Banks, IReadOnlyList<ActionItem> ActionItems);

public sealed record DashboardKpis(
    decimal TrustTotal, decimal AvailableToDisburse, decimal Uncleared, int UnclearedCount,
    decimal TenantPaymentsMtd, decimal ScheduledRent, int Vacancy);

public sealed record OwnerBalancesPanel(IReadOnlyList<OwnerBalanceHeroRow> Rows, OwnerBalancesHeroTotals Totals);

public sealed record OwnerBalanceHeroRow(
    Guid OwnerId, string Name, decimal Operating, decimal Deposits, decimal Total, bool IsRollup);

public sealed record OwnerBalancesHeroTotals(decimal Operating, decimal Deposits, decimal Total);

public sealed record BanksPanel(IReadOnlyList<DashboardBankRow> Rows);

public sealed record DashboardBankRow(Guid BankAccountId, string Name, decimal Book, int UnclearedCount);

public sealed record ActionItem(string Id, string Kind, string Title, string Detail, string Route);
