using System.Text.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Migration;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Onboarding.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Onboarding.Verification;

/// <summary>
/// Orchestrates WP-4 verification (M7): compares operator-supplied AppFolio closing figures to the
/// imported subledger totals derived from the posted journal entries, persists the report snapshot,
/// and enforces the hard sign-off gate.
///
/// <para>
/// <b>Tie-out rule (IsTied):</b> ALL line variances are exactly $0.00 AND ClearingCash == 0 AND
/// ClearingAccrual == 0. Both external match (operator figures vs imported totals) and internal
/// clearing consistency are required.
/// </para>
///
/// <para>
/// <b>Write-once constraint:</b> <c>migration_verifications</c> is <c>RevokeAppendOnly</c> — the
/// runtime role has no UPDATE grant. Sign-off therefore INSERTS a new row with <c>SignedOffBy</c> /
/// <c>SignedOffAt</c> pre-populated, leaving the original unsigned verification row intact
/// (auditable history). The caller receives the signed row's id.
/// </para>
/// </summary>
public sealed class VerificationService(
    DbContext db,
    ISender sender,
    IActorContext actor)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── Verify ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the line-by-line variance report, writes a new <c>migration_verifications</c> row
    /// (never upsert — re-verification appends a new auditable row), and returns the report.
    /// </summary>
    public async Task<VerificationReport> VerifyAsync(
        VerificationRequest request,
        CancellationToken ct)
    {
        // 1. Read imported subledger totals + clearing residuals via the Accounting ISender query.
        //    No cross-module SQL — this dispatches to Accounting's own journal_lines read.
        var data = await sender.Query<MigrationVerificationData>(
            new GetMigrationVerificationData(), ct);

        // 2. Compute line-by-line variances (operator figure − imported total).
        var lines = BuildVarianceLines(request, data);
        var varianceTotal = lines.Sum(l => Math.Abs(l.Variance));

        // 3. IsTied: all variances are zero AND both clearing bases net to zero.
        var isTied = varianceTotal == 0m
            && data.ClearingCash == 0m
            && data.ClearingAccrual == 0m;

        // 4. Build the JSON snapshots.
        var expectedJson = JsonSerializer.Serialize(new
        {
            request.CutoverDate,
            bankBookBalances = request.BankBookBalances,
            ownerEquityTotal = request.OwnerEquityTotal,
            depositLiabilityTotal = request.DepositLiabilityTotal,
        }, JsonOpts);

        var actualJson = JsonSerializer.Serialize(new
        {
            clearingCash = data.ClearingCash,
            clearingAccrual = data.ClearingAccrual,
            ownerEquityTotal = data.OwnerEquityCashTotal,
            depositLiabilityTotal = data.DepositLiabilityTotal,
            bankBookBalances = data.BankBookBalances.Select(b => new
            {
                bankAccountId = b.BankAccountId,
                accountCode = b.AccountCode,
                book = b.Book,
            }),
        }, JsonOpts);

        var reportSnapshot = BuildReportSnapshot(request.CutoverDate, lines, data, isTied);

        // 5. Persist the verification row (write-once; sign-off fields null here).
        var verification = MigrationVerification.Create(
            request.CutoverDate,
            expectedJson,
            actualJson,
            varianceTotal,
            isTied,
            signedOffBy: null,
            signedOffAt: null,
            reportSnapshot);

        db.Set<MigrationVerification>().Add(verification);
        await db.SaveChangesAsync(ct);

        return new VerificationReport(
            verification.Id,
            request.CutoverDate,
            isTied,
            varianceTotal,
            data.ClearingCash,
            data.ClearingAccrual,
            lines,
            reportSnapshot);
    }

    // ── Sign-off ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enforces the hard gate: if the referenced verification row is not tied (IsTied == false),
    /// throws <see cref="MigrationNotTiedException"/> with NO side effect — no DB write, no audit
    /// row. If tied, inserts a new signed row (write-once table — no UPDATE) and writes an audit
    /// event (via the AppDbContext SaveChanges interceptor on the new row insert).
    /// </summary>
    public async Task<SignoffResult> SignOffAsync(Guid verificationId, CancellationToken ct)
    {
        // 1. Load the referenced unsigned verification row.
        var verification = await db.Set<MigrationVerification>()
            .FirstOrDefaultAsync(v => v.Id == verificationId, ct);

        if (verification is null)
        {
            throw new KeyNotFoundException($"Verification {verificationId} not found.");
        }

        // 2. Gate: if NOT tied, throw before any write. Mirrors the StatementNotBalancedException
        //    precedent in LocalStatementDelivery — no side effect on the blocked path.
        if (!verification.IsTied)
        {
            throw new MigrationNotTiedException(verificationId, verification.VarianceTotal);
        }

        // 3. Tied → insert a new signed row. Cannot UPDATE the original row (RevokeAppendOnly).
        //    The new row IS the authoritative signed artifact; the original remains as the unsigned record.
        var signedOffBy = actor.UserId?.ToString() ?? "system";
        var signedOffAt = DateTime.UtcNow;

        var signedRow = MigrationVerification.CreateSignedOff(verification, signedOffBy, signedOffAt);
        db.Set<MigrationVerification>().Add(signedRow);

        // 4. SaveChanges: AppDbContext interceptor auto-audits the INSERT of the signed row with
        //    entity_type = "migration_verifications", action = "insert". The brief calls for an
        //    explicit audit event typed "migration-signed-off" — write it explicitly alongside
        //    the auto-audit so both the generic row audit AND the domain event are recorded.
        var domainAudit = new AuditEvent
        {
            Id = UuidV7.NewId(),
            ActorUserId = actor.UserId,
            EntityType = "migration-signed-off",
            EntityId = signedRow.Id,
            Action = "insert",
            Before = null,
            After = JsonSerializer.Serialize(new
            {
                verificationId = signedRow.Id,
                originalVerificationId = verificationId,
                signedOffBy,
                signedOffAt,
                cutoverDate = signedRow.CutoverDate,
            }, JsonOpts),
        };
        db.Set<AuditEvent>().Add(domainAudit);

        await db.SaveChangesAsync(ct);

        return new SignoffResult(signedRow.Id, signedOffAt);
    }

    // ── Variance computation ──────────────────────────────────────────────────────────────────────

    private static List<VarianceLine> BuildVarianceLines(
        VerificationRequest request,
        MigrationVerificationData data)
    {
        var lines = new List<VarianceLine>();

        // Owner equity total (cash basis): operator vs imported
        lines.Add(new VarianceLine(
            "owner_equity_cash",
            "Owner Equity (Cash)",
            request.OwnerEquityTotal,
            data.OwnerEquityCashTotal,
            request.OwnerEquityTotal - data.OwnerEquityCashTotal));

        // Deposit liability total (cash basis): operator vs imported
        lines.Add(new VarianceLine(
            "deposit_liability_cash",
            "Security Deposits Held (Cash)",
            request.DepositLiabilityTotal,
            data.DepositLiabilityTotal,
            request.DepositLiabilityTotal - data.DepositLiabilityTotal));

        // Per-bank book balance: match by BankAccountId
        var importedBankMap = data.BankBookBalances
            .ToDictionary(b => b.BankAccountId);

        foreach (var operatorBank in request.BankBookBalances)
        {
            var imported = importedBankMap.TryGetValue(operatorBank.BankAccountId, out var b) ? b.Book : 0m;
            lines.Add(new VarianceLine(
                $"bank:{operatorBank.BankAccountId:N}",
                $"Bank Book Balance ({operatorBank.AccountCode ?? operatorBank.BankAccountId.ToString("N")[..8]})",
                operatorBank.ExpectedBook,
                imported,
                operatorBank.ExpectedBook - imported));
        }

        // Banks in imported data but not in operator figures — flag as unexpected
        var operatorBankIds = request.BankBookBalances.Select(b => b.BankAccountId).ToHashSet();
        foreach (var importedBank in data.BankBookBalances.Where(b => !operatorBankIds.Contains(b.BankAccountId)))
        {
            lines.Add(new VarianceLine(
                $"bank:{importedBank.BankAccountId:N}",
                $"Bank Book Balance (imported, no operator figure: {importedBank.AccountCode})",
                0m,
                importedBank.Book,
                -importedBank.Book));
        }

        return lines;
    }

    private static string BuildReportSnapshot(
        DateOnly cutoverDate,
        List<VarianceLine> lines,
        MigrationVerificationData data,
        bool isTied)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Migration Verification Report — Cutover {cutoverDate:yyyy-MM-dd}");
        sb.AppendLine($"Status: {(isTied ? "TIED ✓" : "NOT TIED ✗")}");
        sb.AppendLine();
        sb.AppendLine("Internal Clearing Residuals:");
        sb.AppendLine($"  Cash basis:    {data.ClearingCash,12:0.00} {(data.ClearingCash == 0m ? "✓" : "✗")}");
        sb.AppendLine($"  Accrual basis: {data.ClearingAccrual,12:0.00} {(data.ClearingAccrual == 0m ? "✓" : "✗")}");
        sb.AppendLine();
        sb.AppendLine("Line-by-Line Variance:");
        foreach (var line in lines)
        {
            var status = line.Variance == 0m ? "✓" : "✗";
            sb.AppendLine(
                $"  {status} {line.Label,-48} Expected: {line.Expected,12:0.00}  Actual: {line.Actual,12:0.00}  Variance: {line.Variance,12:0.00}");
        }

        return sb.ToString();
    }
}

// ── Request / result types ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Operator-supplied AppFolio closing figures for the <c>POST /api/onboarding/verification</c>
/// endpoint.
/// </summary>
public sealed record VerificationRequest(
    DateOnly CutoverDate,
    decimal OwnerEquityTotal,
    decimal DepositLiabilityTotal,
    IReadOnlyList<OperatorBankBalance> BankBookBalances);

/// <summary>One bank account's closing figure from the operator's AppFolio export.</summary>
public sealed record OperatorBankBalance(
    Guid BankAccountId,
    decimal ExpectedBook,
    string? AccountCode = null);

/// <summary>The computed verification report returned to the caller.</summary>
public sealed record VerificationReport(
    Guid VerificationId,
    DateOnly CutoverDate,
    bool IsTied,
    decimal VarianceTotal,
    decimal ClearingCash,
    decimal ClearingAccrual,
    IReadOnlyList<VarianceLine> Lines,
    string ReportSnapshot);

/// <summary>One line in the variance report.</summary>
public sealed record VarianceLine(
    string Key,
    string Label,
    decimal Expected,
    decimal Actual,
    decimal Variance);

/// <summary>Returned on successful sign-off.</summary>
public sealed record SignoffResult(Guid SignedVerificationId, DateTime SignedOffAt);
