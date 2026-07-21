using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Migration;

/// <summary>
/// Query: every per-position opening entry (<c>event_type = 'OpeningBalance'</c>, the M7/ADR-020
/// import path) with its REAL (non-clearing) leg and whether a linked reversal exists. Consumed by
/// the WP-7 supersede host via <c>ISender</c> — Accounting reading its own tables (ADR-007 clean,
/// same pattern as <see cref="GetMigrationVerificationData"/>). The batched
/// <c>BalanceForward</c> seed path and <c>EntryVoided</c> reversals are deliberately excluded:
/// only per-position entries are supersedable, and voids are lifecycle markers, not positions.
/// </summary>
public sealed record GetOpeningPositions : IQuery<OpeningPositionsResponse>;

public sealed record OpeningPositionsResponse(IReadOnlyList<OpeningEntry> Entries);

/// <summary>One opening entry's real leg. <see cref="IsReversed"/> = a reversal references it.</summary>
public sealed record OpeningEntry(
    Guid EntryId, string SourceRef, DateOnly EntryDate, bool IsReversed,
    string AccountCode, decimal? Debit, decimal? Credit, string Basis,
    Guid? PropertyId, Guid? UnitId, Guid? OwnerId, Guid? TenantId, Guid? BankAccountId);

internal sealed class GetOpeningPositionsHandler(DbContext db)
    : IQueryHandler<GetOpeningPositions, OpeningPositionsResponse>
{
    public async Task<OpeningPositionsResponse> Handle(GetOpeningPositions query, CancellationToken ct)
    {
        // One row per entry: join to the single non-clearing line (every OpeningBalance entry has
        // exactly two lines — real leg + migration_clearing contra, AccountingEventService.cs:70-79).
        var rows = await db.Database.SqlQuery<OpeningRow>(
            $"""
            SELECT e.id            AS entry_id,
                   e.source_ref,
                   e.entry_date,
                   EXISTS (SELECT 1 FROM journal_entries r
                           WHERE r.reverses_entry_id = e.id) AS is_reversed,
                   a.code          AS account_code,
                   jl.debit,
                   jl.credit,
                   jl.basis,
                   jl.property_id,
                   jl.unit_id,
                   jl.owner_id,
                   jl.tenant_id,
                   jl.bank_account_id
            FROM journal_entries e
            JOIN journal_lines jl ON jl.entry_id = e.id
            JOIN accounts a ON a.id = jl.account_id
            WHERE e.event_type = 'OpeningBalance'
              AND jl.account_class <> 'migration_clearing'
            ORDER BY e.source_ref
            """).ToListAsync(ct);

        var entries = rows
            .Select(r => new OpeningEntry(
                r.EntryId, r.SourceRef, r.EntryDate, r.IsReversed, r.AccountCode,
                r.Debit, r.Credit, r.Basis, r.PropertyId, r.UnitId, r.OwnerId, r.TenantId,
                r.BankAccountId))
            .ToList();

        return new OpeningPositionsResponse(entries);
    }

    private sealed record OpeningRow(
        Guid EntryId, string SourceRef, DateOnly EntryDate, bool IsReversed,
        string AccountCode, decimal? Debit, decimal? Credit, string Basis,
        Guid? PropertyId, Guid? UnitId, Guid? OwnerId, Guid? TenantId, Guid? BankAccountId);
}
