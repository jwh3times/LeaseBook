using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

internal sealed record OpenReceivableCharge(
    Guid EntryId,
    Guid TenantId,
    string EventType,
    string? SourceRef,
    DateOnly DueDate,
    decimal RemainingAmount);

internal sealed record ReceivableAllocationSnapshot(
    IReadOnlyList<OpenReceivableCharge> OpenCharges,
    IReadOnlyDictionary<Guid, decimal> UnappliedCredits);

/// <summary>
/// Replays tenant-receivable activity as of a date and computes the shared FIFO allocation state
/// consumed by delinquency aging and rent-obligation lookup.
/// </summary>
internal static class ReceivableAllocationReader
{
    public static async Task<ReceivableAllocationSnapshot> ReadAsync(
        DbContext db,
        DateOnly asOf,
        CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReceivableAllocationRow>(
            $"""
            WITH receivable_entries AS (
                SELECT e.id AS entry_id,
                       jl.tenant_id,
                       e.event_type,
                       e.source_ref,
                       COALESCE(e.due_date, e.entry_date) AS due_date,
                       e.posted_at,
                       SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0)) AS net_owed
                FROM journal_lines jl
                JOIN journal_entries e ON e.id = jl.entry_id
                WHERE jl.account_class = 'tenant_receivable'
                  AND jl.basis IN ('accrual', 'both')
                  AND jl.tenant_id IS NOT NULL
                  AND e.entry_date <= {asOf}
                  AND e.reverses_entry_id IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM journal_entries reversal
                      WHERE reversal.reverses_entry_id = e.id
                        AND reversal.entry_date <= {asOf})
                GROUP BY e.id, jl.tenant_id, e.event_type, e.source_ref,
                         e.due_date, e.entry_date, e.posted_at
            ),
            tenant_balances AS (
                SELECT tenant_id,
                       SUM(GREATEST(net_owed, 0)) AS total_charges,
                       SUM(GREATEST(-net_owed, 0)) AS total_credits
                FROM receivable_entries
                GROUP BY tenant_id
            ),
            ordered_charges AS (
                SELECT entry.entry_id,
                       entry.tenant_id,
                       entry.event_type,
                       entry.source_ref,
                       entry.due_date,
                       entry.posted_at,
                       entry.net_owed,
                       balances.total_credits,
                       SUM(entry.net_owed) OVER (
                           PARTITION BY entry.tenant_id
                           ORDER BY entry.due_date, entry.posted_at, entry.entry_id
                           ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                       ) AS cumulative_charges,
                       COALESCE(SUM(entry.net_owed) OVER (
                           PARTITION BY entry.tenant_id
                           ORDER BY entry.due_date, entry.posted_at, entry.entry_id
                           ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
                       ), 0) AS prior_charges
                FROM receivable_entries entry
                JOIN tenant_balances balances ON balances.tenant_id = entry.tenant_id
                WHERE entry.net_owed > 0
            ),
            computed_charges AS (
                SELECT entry_id,
                       tenant_id,
                       event_type,
                       source_ref,
                       due_date,
                       GREATEST(cumulative_charges - total_credits, 0)
                           - GREATEST(prior_charges - total_credits, 0) AS remaining_amount
                FROM ordered_charges
            ),
            open_charges AS (
                SELECT *
                FROM computed_charges
                WHERE remaining_amount > 0
            )
            SELECT balances.tenant_id,
                   charges.entry_id,
                   charges.event_type,
                   charges.source_ref,
                   charges.due_date,
                   COALESCE(charges.remaining_amount, 0) AS remaining_amount,
                   GREATEST(balances.total_credits - balances.total_charges, 0) AS unapplied_credit
            FROM tenant_balances balances
            LEFT JOIN open_charges charges ON charges.tenant_id = balances.tenant_id
            ORDER BY balances.tenant_id, charges.due_date, charges.entry_id
            """).ToListAsync(ct);

        var openCharges = rows
            .Where(row => row.EntryId.HasValue)
            .Select(row => new OpenReceivableCharge(
                row.EntryId!.Value,
                row.TenantId,
                row.EventType!,
                row.SourceRef,
                row.DueDate!.Value,
                row.RemainingAmount))
            .ToList();

        var unappliedCredits = rows
            .Where(row => row.UnappliedCredit > 0)
            .GroupBy(row => row.TenantId)
            .ToDictionary(group => group.Key, group => group.First().UnappliedCredit);

        return new ReceivableAllocationSnapshot(openCharges, unappliedCredits);
    }

    private sealed record ReceivableAllocationRow(
        Guid TenantId,
        Guid? EntryId,
        string? EventType,
        string? SourceRef,
        DateOnly? DueDate,
        decimal RemainingAmount,
        decimal UnappliedCredit);
}
