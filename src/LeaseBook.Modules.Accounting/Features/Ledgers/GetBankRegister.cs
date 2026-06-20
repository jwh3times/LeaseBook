using FluentValidation;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Persistence;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Ledgers;

/// <summary>
/// The bank register for one bank account (§B.4 / P69): every journal line carrying that bank dimension,
/// projected as a statement-style row (deposit = debit, withdrawal = credit, clearance status), filterable
/// and paginated. A pure projection of the journal <b>within Accounting</b> — own-module raw SQL on the
/// ambient scoped connection, crossing no boundary (ADR-007). Property <i>labels</i> are resolved at the
/// endpoint layer through a Directory port, so this query returns <c>property_id</c> only.
/// </summary>
public sealed record GetBankRegister(
    Guid BankAccountId,
    Guid? PropertyId = null,
    RegisterTypeFilter Type = RegisterTypeFilter.All,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 50) : IQuery<RegisterResponse>;

public enum RegisterTypeFilter
{
    All,
    Deposits,
    Withdrawals,
}

public sealed class GetBankRegisterValidator : AbstractValidator<GetBankRegister>
{
    public GetBankRegisterValidator()
    {
        RuleFor(q => q.BankAccountId).NotEmpty();
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 200);
    }
}

public sealed record RegisterResponse(IReadOnlyList<RegisterRow> Rows, int Total, RegisterTotals Totals);

public sealed record RegisterRow(
    Guid JournalLineId, DateOnly Date, string? Description, Guid? PropertyId,
    decimal? Deposit, decimal? Withdrawal, BankLineStatus Status);

public sealed record RegisterTotals(
    decimal Book, decimal Cleared, decimal Uncleared, int UnclearedCount,
    decimal DepositsInView, decimal WithdrawalsInView);

internal sealed class GetBankRegisterHandler(DbContext db) : IQueryHandler<GetBankRegister, RegisterResponse>
{
    public async Task<RegisterResponse> Handle(GetBankRegister query, CancellationToken ct)
    {
        var deposits = query.Type == RegisterTypeFilter.Deposits;
        var withdrawals = query.Type == RegisterTypeFilter.Withdrawals;
        var offset = (query.Page - 1) * query.PageSize;
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();

        // Bank-register rows: each journal line on this bank (cash/both — bank movements are never
        // accrual-only). The window aggregates carry the filtered totals + count on every row.
        var rows = await db.Database.SqlQuery<RegisterSqlRow>(
            $"""
            WITH bank_lines AS (
                SELECT jl.id AS journal_line_id,
                       e.entry_date AS date,
                       e.description,
                       jl.property_id,
                       jl.debit AS deposit,
                       jl.credit AS withdrawal,
                       COALESCE(s.status, 'uncleared') AS status
                FROM journal_lines jl
                JOIN journal_entries e ON e.id = jl.entry_id
                LEFT JOIN bank_line_status s ON s.journal_line_id = jl.id
                WHERE jl.bank_account_id = {query.BankAccountId}
                  -- the register is lines posted to the bank ACCOUNT, not every line that merely carries
                  -- the bank dimension for attribution (owner-equity / pm-income lines do too).
                  AND jl.account_class IN ('trust_bank', 'pm_operating_bank')
                  AND jl.basis IN ('cash', 'both')
                  AND ({query.PropertyId}::uuid IS NULL OR jl.property_id = {query.PropertyId})
                  AND ({query.From}::date IS NULL OR e.entry_date >= {query.From})
                  AND ({query.To}::date IS NULL OR e.entry_date <= {query.To})
                  AND (NOT {deposits} OR jl.debit IS NOT NULL)
                  AND (NOT {withdrawals} OR jl.credit IS NOT NULL)
                  AND ({search}::text IS NULL OR e.description ILIKE '%' || {search}::text || '%')
            )
            SELECT journal_line_id,
                   date,
                   description,
                   property_id,
                   deposit,
                   withdrawal,
                   status,
                   COUNT(*) OVER()::int AS total,
                   COALESCE(SUM(deposit) OVER(), 0) AS deposits_in_view,
                   COALESCE(SUM(withdrawal) OVER(), 0) AS withdrawals_in_view
            FROM bank_lines
            ORDER BY date DESC, journal_line_id DESC
            LIMIT {query.PageSize} OFFSET {offset}
            """).ToListAsync(ct);

        // Account totals span the whole account regardless of the view filters (the balance strip).
        var totals = (await db.Database.SqlQuery<RegisterTotalsSqlRow>(
            $"""
            SELECT
                COALESCE(SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0)), 0) AS book,
                COALESCE(SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0))
                         FILTER (WHERE COALESCE(s.status, 'uncleared') IN ('cleared', 'reconciled')), 0) AS cleared,
                COUNT(*) FILTER (WHERE COALESCE(s.status, 'uncleared') = 'uncleared')::int AS uncleared_count
            FROM journal_lines jl
            LEFT JOIN bank_line_status s ON s.journal_line_id = jl.id
            WHERE jl.bank_account_id = {query.BankAccountId}
              AND jl.account_class IN ('trust_bank', 'pm_operating_bank')
              AND jl.basis IN ('cash', 'both')
            """).ToListAsync(ct)).Single();

        var total = rows.Count > 0 ? rows[0].Total : 0;
        var depositsInView = rows.Count > 0 ? rows[0].DepositsInView : 0m;
        var withdrawalsInView = rows.Count > 0 ? rows[0].WithdrawalsInView : 0m;

        var mapped = rows
            .Select(r => new RegisterRow(
                r.JournalLineId, r.Date, r.Description, r.PropertyId,
                r.Deposit, r.Withdrawal, BankLineStatusConverter.FromDb(r.Status)))
            .ToList();

        return new RegisterResponse(
            mapped,
            total,
            new RegisterTotals(
                totals.Book, totals.Cleared, totals.Book - totals.Cleared, totals.UnclearedCount,
                depositsInView, withdrawalsInView));
    }

    private sealed record RegisterSqlRow(
        Guid JournalLineId, DateOnly Date, string? Description, Guid? PropertyId,
        decimal? Deposit, decimal? Withdrawal, string Status, int Total,
        decimal DepositsInView, decimal WithdrawalsInView);

    private sealed record RegisterTotalsSqlRow(decimal Book, decimal Cleared, int UnclearedCount);
}
