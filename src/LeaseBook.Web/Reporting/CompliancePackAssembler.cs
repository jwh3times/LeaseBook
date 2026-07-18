using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Web.Audit;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// The composed trust-compliance pack for one trust account × period (WP-8): four audit-shaped
/// artifacts plus a cover that ties the period-end trust equation to the ledger movement. Pure data —
/// no rendering, no side effects. <see cref="ArtifactNames"/> is the closed set of four artifacts; the
/// PM-facing management-fee report is deliberately absent (owner-facing artifacts carry no PM income).
/// </summary>
public sealed record CompliancePack(
    CompliancePackCover Cover,
    IReadOnlyList<RegisterRow> TrustLedger,
    IReadOnlyList<DepositRegisterRow> DepositRegister,
    IReadOnlyList<CompliancePackReconciliation> ReconciliationHistory,
    IReadOnlyList<AuditExtractRow> AuditTrail)
{
    public static readonly IReadOnlyList<string> ArtifactNames =
    [
        "trust-account-ledger",
        "security-deposit-register",
        "reconciliation-history",
        "audit-log-extract",
    ];
}

/// <summary>
/// The pack cover/index tie-out: opening book (as of the day before the period) + the period's ledger
/// movement must equal <see cref="ClosingEquation"/>.Book (the period-end trust equation for this bank),
/// whose Variance is 0.00.
/// </summary>
public sealed record CompliancePackCover(
    Guid BankAccountId, string BankName, string BankPurpose,
    DateOnly PeriodStart, DateOnly PeriodEnd,
    decimal OpeningBook, TrustEquationRow ClosingEquation);

/// <summary>One finalized reconciliation in the period, with its immutable stored report snapshot (verbatim).</summary>
public sealed record CompliancePackReconciliation(
    Guid Id, int Year, int Month, decimal StatementEndingBalance, DateTime? FinalizedAt, string? ReportJson);

/// <summary>
/// Composes the pack for a trust account × period by dispatching existing Accounting/Directory reads via
/// <see cref="ISender"/> (the host composition-root pattern, ADR-016) plus the host audit extract. Reads
/// only — it computes no new figures and writes nothing; the endpoint records the generation audit event.
/// </summary>
public sealed class CompliancePackAssembler(ISender sender, AuditExtractReader auditReader)
{
    public async Task<CompliancePack> AssembleAsync(
        Guid bankAccountId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var bank = await sender.Query(new GetBankAccount(bankAccountId), ct)
            ?? throw new KeyNotFoundException($"Bank account {bankAccountId} not found.");

        // Cover tie-out: the period-end trust equation for this bank, and the opening book (the day
        // before the period). Both are date-bounded reads of the same balanced journal.
        var closing = FindBank(await sender.Query(new GetTrustEquation(to), ct), bankAccountId)
                      ?? new TrustEquationRow(bankAccountId, 0m, 0m, 0m, 0m, 0m, 0m);
        var openingBook =
            FindBank(await sender.Query(new GetTrustEquation(from.AddDays(-1)), ct), bankAccountId)?.Book ?? 0m;

        // Trust-account ledger: every register row in the period (paged — the register caps at 200/page,
        // and a compliance pack must never silently truncate).
        var ledger = new List<RegisterRow>();
        for (var page = 1; ; page++)
        {
            var slice = await sender.Query(
                new GetBankRegister(bankAccountId, From: from, To: to, Page: page, PageSize: 200), ct);
            ledger.AddRange(slice.Rows);
            if (ledger.Count >= slice.Total || slice.Rows.Count == 0)
            {
                break;
            }
        }

        // Security-deposit register as of the period end, scoped to this trust account.
        var deposits = await sender.Query(new GetDepositRegister(bankAccountId, to), ct);

        // Reconciliation history: finalized reconciliations whose month falls in the period, each with its
        // immutable stored report snapshot (never recomputed).
        var history = await sender.Query(new GetReconciliationHistory(bankAccountId), ct);
        var recons = new List<CompliancePackReconciliation>();
        foreach (var r in history.Rows.Where(r => r.HasReport && InPeriod(r.Year, r.Month, from, to)))
        {
            var report = await sender.Query(new GetReconciliationReport(r.Id), ct);
            recons.Add(new CompliancePackReconciliation(
                r.Id, r.Year, r.Month, r.StatementEndingBalance, r.FinalizedAt, report?.ReportJson));
        }

        // Money-touching audit trail for the period.
        var audit = await auditReader.GetAsync(from, to, ct);

        var cover = new CompliancePackCover(bank.Id, bank.Name, bank.Purpose, from, to, openingBook, closing);
        return new CompliancePack(cover, ledger, deposits.Rows, recons, audit.Rows);
    }

    private static TrustEquationRow? FindBank(TrustEquationResponse equation, Guid bankId) =>
        equation.Rows.FirstOrDefault(r => r.BankAccountId == bankId);

    private static bool InPeriod(int year, int month, DateOnly from, DateOnly to)
    {
        var key = (year * 12) + (month - 1);
        return key >= (from.Year * 12) + (from.Month - 1)
            && key <= (to.Year * 12) + (to.Month - 1);
    }
}
