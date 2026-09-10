using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Statements;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Diagnostics;

/// <summary>
/// Executable swept checks (§C.7) over the ambient org's journal via raw SQL on the scoped connection
/// (RLS-scoped, M-E11). Shared by the CLI sweep and the test harness; this is the future nightly
/// sweep body (P33).
/// </summary>
internal sealed class InvariantChecks(DbContext db) : IInvariantChecks
{
    public async Task<IReadOnlyList<InvariantViolation>> CheckCoreAsync(CancellationToken ct)
    {
        var violations = new List<InvariantViolation>();
        violations.AddRange(await CheckEntriesBalanceAsync(ct));
        violations.AddRange(await CheckTrustEquationAsync(ct));
        violations.AddRange(await CheckPmIncomeIsolationAsync(ct));
        violations.AddRange(await CheckDepositLiabilitiesNonNegativeAsync(ct));
        violations.AddRange(await CheckDepositAttributionSymmetricAsync(ct));
        violations.AddRange(await CheckMigrationClearingBalancedAsync(ct));
        violations.AddRange(await CheckStatementSectionCoverageAsync(ct));
        return violations;
    }

    // I1: for cash and accrual, debits == credits over {basis, both} for every entry.
    public async Task<IReadOnlyList<InvariantViolation>> CheckEntriesBalanceAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<UnbalancedEntry>(
            $"""
            SELECT entry_id, basis_name, sum_debit, sum_credit FROM (
                SELECT entry_id, 'cash' AS basis_name,
                       SUM(COALESCE(debit, 0)) FILTER (WHERE basis IN ('cash', 'both')) AS sum_debit,
                       SUM(COALESCE(credit, 0)) FILTER (WHERE basis IN ('cash', 'both')) AS sum_credit
                FROM journal_lines GROUP BY entry_id
                UNION ALL
                SELECT entry_id, 'accrual',
                       SUM(COALESCE(debit, 0)) FILTER (WHERE basis IN ('accrual', 'both')),
                       SUM(COALESCE(credit, 0)) FILTER (WHERE basis IN ('accrual', 'both'))
                FROM journal_lines GROUP BY entry_id
            ) s
            WHERE sum_debit <> sum_credit
            """).ToListAsync(ct);

        return rows
            .Select(r => new InvariantViolation("I1",
                $"entry {r.EntryId} does not balance in {r.BasisName}: debits {r.SumDebit:0.00} != credits {r.SumCredit:0.00}"))
            .ToList();
    }

    // I2: book(B) == owner equity + deposit liabilities + prepayments + held PM fees, per trust bank.
    public async Task<IReadOnlyList<InvariantViolation>> CheckTrustEquationAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<EquationVariance>(
            $"""
            SELECT bank_account_id, book - (owner_equity + deposit_liabilities + prepayments + held_pm_fees) AS variance
            FROM (
                SELECT jl.bank_account_id,
                       COALESCE(SUM(CASE WHEN jl.account_class = 'trust_bank'
                                         THEN COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0) ELSE 0 END), 0) AS book,
                       COALESCE(SUM(CASE WHEN jl.account_class = 'owner_equity'
                                         THEN COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0) ELSE 0 END), 0) AS owner_equity,
                       COALESCE(SUM(CASE WHEN a.code = 'security_deposits_held'
                                         THEN COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0) ELSE 0 END), 0) AS deposit_liabilities,
                       COALESCE(SUM(CASE WHEN a.code = 'tenant_prepayments'
                                         THEN COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0) ELSE 0 END), 0) AS prepayments,
                       COALESCE(SUM(CASE WHEN jl.account_class = 'pm_income'
                                         THEN COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0) ELSE 0 END), 0) AS held_pm_fees
                FROM journal_lines jl JOIN accounts a ON a.id = jl.account_id
                WHERE jl.basis IN ('cash', 'both')
                  AND jl.bank_account_id IN (SELECT bank_account_id FROM accounts WHERE class = 'trust_bank')
                GROUP BY jl.bank_account_id
            ) e
            WHERE book - (owner_equity + deposit_liabilities + prepayments + held_pm_fees) <> 0
            """).ToListAsync(ct);

        return rows
            .Select(r => new InvariantViolation("I2",
                $"trust equation off by {r.Variance:0.00} on bank {r.BankAccountId}"))
            .ToList();
    }

    // I3: no pm_income line carries an owner dimension (DB CHECK backs this; the sweep proves zero rows).
    public async Task<IReadOnlyList<InvariantViolation>> CheckPmIncomeIsolationAsync(CancellationToken ct)
    {
        var ids = await db.Database.SqlQuery<Guid>(
            $"""SELECT id AS "Value" FROM journal_lines WHERE account_class = 'pm_income' AND owner_id IS NOT NULL""")
            .ToListAsync(ct);

        return ids
            .Select(id => new InvariantViolation("I3", $"pm_income line {id} carries an owner_id"))
            .ToList();
    }

    // I4: held deposit and held prepayment are each ≥ 0 per tenant.
    public async Task<IReadOnlyList<InvariantViolation>> CheckDepositLiabilitiesNonNegativeAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<NegativeLiability>(
            $"""
            SELECT jl.tenant_id, a.code, SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS held
            FROM journal_lines jl JOIN accounts a ON a.id = jl.account_id
            WHERE a.code IN ('security_deposits_held', 'tenant_prepayments') AND jl.basis IN ('cash', 'both')
            GROUP BY jl.tenant_id, a.code
            HAVING SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) < 0
            """).ToListAsync(ct);

        return rows
            .Select(r => new InvariantViolation("I4",
                $"held {r.Code} for tenant {r.TenantId} is negative ({r.Held:0.00})"))
            .ToList();
    }

    // I9: migration_clearing nets to $0 per basis — non-zero residual is a quantified import discrepancy
    // (ADR-020 / M7). The invariant is vacuous (no rows) for orgs that haven't imported, so it is safe
    // to include in the core sweep for all orgs.
    public async Task<IReadOnlyList<InvariantViolation>> CheckMigrationClearingBalancedAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ClearingVariance>(
            $"""
            SELECT basis_name, net
            FROM (
                SELECT 'cash' AS basis_name,
                       SUM(COALESCE(debit, 0) - COALESCE(credit, 0)) FILTER (WHERE basis IN ('cash', 'both')) AS net
                FROM journal_lines WHERE account_class = 'migration_clearing'
                UNION ALL
                SELECT 'accrual',
                       SUM(COALESCE(debit, 0) - COALESCE(credit, 0)) FILTER (WHERE basis IN ('accrual', 'both'))
                FROM journal_lines WHERE account_class = 'migration_clearing'
            ) s
            WHERE net IS NOT NULL AND net <> 0
            """).ToListAsync(ct);

        return rows
            .Select(r => new InvariantViolation("I9",
                $"migration_clearing does not net to $0 in {r.BasisName}: residual {r.Net:0.00}"))
            .ToList();
    }

    // I7: held security deposit is ≥ 0 per (tenant, owner-attribution bucket) — a strengthening of I4 that
    // catches dimension asymmetry. A disposition that drops the owner dim its collection carried leaves the
    // tenant's total at zero (I4 clean) but drives the owner-null bucket negative, so the owner-attributed
    // column never comes down (the WP-13 §12.11 defect). Owner-null-throughout deposits — the demo org's
    // aggregate unattributed position — never go negative and stay clean.
    public async Task<IReadOnlyList<InvariantViolation>> CheckDepositAttributionSymmetricAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<AttributionBucket>(
            $"""
            SELECT jl.tenant_id, jl.owner_id, SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS held
            FROM journal_lines jl JOIN accounts a ON a.id = jl.account_id
            WHERE a.code = 'security_deposits_held' AND jl.basis IN ('cash', 'both')
            GROUP BY jl.tenant_id, jl.owner_id
            HAVING SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) < 0
            """).ToListAsync(ct);

        return rows
            .Select(r => new InvariantViolation("I7",
                $"held security deposit for tenant {r.TenantId} under owner "
                + $"{(r.OwnerId is null ? "(unattributed)" : r.OwnerId.ToString())} is negative ({r.Held:0.00}) "
                + "— a disposition released a different owner bucket than the collection credited"))
            .ToList();
    }

    // I8: every event_type carrying an owner-attributed owner_equity line has a statement section.
    // Unlike I1-I7 this asserts over the *shape* of the journal rather than its balances, because the
    // defect it catches is a reachability one: StatementSectionMap.Section throws
    // UncategorizedEventException on an unmapped type, so a new owner-crediting event makes every
    // statement for every affected owner fail to render — and today that surfaces only when a human
    // asks for one of those statements.
    //
    // Deliberately a set difference over event types rather than a per-(owner, period, basis) run of
    // the statement handler. Three reasons: the risk is a property of the event-type set, not of any
    // period, so one query covers all owners and all history; the handler would *throw* on the very
    // org that has the defect, and the sweep loop has no per-org exception isolation, so org N's
    // breach would stop orgs N+1..M from being checked at all; and the exception carries only the
    // event type, so it would report strictly less than this query does.
    //
    // The statement's own Variance is NOT swept. Given the three queries in GetOwnerStatementData it
    // is unfalsifiable by any data state — begins ∪ rows is exactly the end-balance predicate — so a
    // nightly variance check could never go red. It is falsifiable only by an inconsistent source
    // edit, which is what StatementInvariantTests asserts in CI.
    public async Task<IReadOnlyList<InvariantViolation>> CheckStatementSectionCoverageAsync(CancellationToken ct)
    {
        var covered = StatementSectionMap.CoveredEventTypes.ToArray();

        var rows = await db.Database.SqlQuery<UncoveredEventType>(
            $"""
            SELECT COALESCE(orig.event_type, e.event_type) AS event_type,
                   COUNT(*) AS line_count,
                   MIN(e.id::text) AS sample_entry_id
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            LEFT JOIN journal_entries orig ON orig.id = e.reverses_entry_id
            WHERE jl.account_class = 'owner_equity' AND jl.owner_id IS NOT NULL
              AND COALESCE(orig.event_type, e.event_type) <> ALL({covered})
            GROUP BY COALESCE(orig.event_type, e.event_type)
            ORDER BY COALESCE(orig.event_type, e.event_type)
            """).ToListAsync(ct);

        return rows
            .Select(r => new InvariantViolation("I8",
                $"event_type '{r.EventType}' posts owner-attributed owner_equity lines but has no "
                + $"statement section — {r.LineCount} line(s), e.g. entry {r.SampleEntryId}; every "
                + "owner statement covering one of them fails to render"))
            .ToList();
    }

    private sealed record UnbalancedEntry(Guid EntryId, string BasisName, decimal SumDebit, decimal SumCredit);

    private sealed record EquationVariance(Guid BankAccountId, decimal Variance);

    private sealed record NegativeLiability(Guid? TenantId, string Code, decimal Held);

    private sealed record AttributionBucket(Guid? TenantId, Guid? OwnerId, decimal Held);

    private sealed record ClearingVariance(string BasisName, decimal Net);

    private sealed record UncoveredEventType(string EventType, int LineCount, string SampleEntryId);
}
