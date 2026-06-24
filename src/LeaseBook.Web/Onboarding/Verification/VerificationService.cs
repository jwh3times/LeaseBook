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
        // 1. Read imported subledger totals + clearing residuals from the *current* journal, then
        //    compute the line-by-line variance + tie-out against the operator's figures.
        var tieOut = await ComputeTieOutAsync(request, ct);
        var data = tieOut.Data;
        var lines = tieOut.Lines;
        var varianceTotal = tieOut.VarianceTotal;
        var isTied = tieOut.IsTied;

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
    /// Enforces the hard gate. <b>Re-derives the tie-out against the *current* journal</b> (not the
    /// stored <c>IsTied</c> flag, which was frozen at <see cref="VerifyAsync"/> time): the journal can
    /// drift after verification (e.g. a later balance import makes clearing non-zero), and signing a
    /// frozen "TIED ✓" snapshot over a now-untied journal would be fiduciarily wrong. If the recomputed
    /// tie-out fails, throws <see cref="MigrationNotTiedException"/> (→ 409) with NO side effect — no DB
    /// write, no audit row — BEFORE inserting the signed row. Mirrors the
    /// <c>StatementNotBalancedException</c> precedent (M5/WP-04). If it still ties, inserts a new signed
    /// row (write-once table — no UPDATE) and writes a <c>migration-signed-off</c> audit event.
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

        // 2. Re-derive tie-out FRESH against the current journal + the row's stored operator figures.
        //    Reading the expected figures from ExpectedJson (not trusting the frozen IsTied flag) closes
        //    the drift window: if a later import made clearing non-zero, this recompute catches it.
        var expected = DeserializeExpected(verification.ExpectedJson);
        var tieOut = await ComputeTieOutAsync(expected, ct);

        // 3. Gate: if it no longer ties, throw before any write. No side effect on the blocked path.
        if (!tieOut.IsTied)
        {
            throw new MigrationNotTiedException(
                verificationId, tieOut.VarianceTotal, tieOut.Data.ClearingCash, tieOut.Data.ClearingAccrual);
        }

        // 4. Still ties → insert a new signed row. Cannot UPDATE the original row (RevokeAppendOnly).
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

    // ── Tie-out computation (shared by VerifyAsync + SignOffAsync) ─────────────────────────────────

    /// <summary>
    /// Reads the *current* imported subledger totals + clearing residuals from the journal (via the
    /// Accounting <see cref="GetMigrationVerificationData"/> ISender query — no cross-module SQL),
    /// computes the line-by-line variance against <paramref name="request"/>'s operator figures, and
    /// derives tie-out. <b>IsTied</b> requires ALL line variances exactly $0.00 AND ClearingCash == 0
    /// AND ClearingAccrual == 0 (external match AND internal clearing consistency).
    /// </summary>
    private async Task<TieOutResult> ComputeTieOutAsync(VerificationRequest request, CancellationToken ct)
    {
        var data = await sender.Query<MigrationVerificationData>(
            new GetMigrationVerificationData(), ct);

        var lines = BuildVarianceLines(request, data);
        var varianceTotal = lines.Sum(l => Math.Abs(l.Variance));

        var isTied = varianceTotal == 0m
            && data.ClearingCash == 0m
            && data.ClearingAccrual == 0m;

        return new TieOutResult(data, lines, varianceTotal, isTied);
    }

    /// <summary>
    /// Rebuilds a <see cref="VerificationRequest"/> from a verification row's stored
    /// <c>ExpectedJson</c> so sign-off can recompute variance against the operator figures captured at
    /// verification time. Round-trips the same shape <see cref="VerifyAsync"/> serialized.
    /// </summary>
    private static VerificationRequest DeserializeExpected(string expectedJson)
    {
        using var doc = JsonDocument.Parse(expectedJson);
        var root = doc.RootElement;

        var cutoverDate = DateOnly.Parse(
            root.GetProperty("cutoverDate").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        var ownerEquity = root.GetProperty("ownerEquityTotal").GetDecimal();
        var depositLiability = root.GetProperty("depositLiabilityTotal").GetDecimal();

        var banks = new List<OperatorBankBalance>();
        if (root.TryGetProperty("bankBookBalances", out var banksEl)
            && banksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in banksEl.EnumerateArray())
            {
                banks.Add(new OperatorBankBalance(
                    b.GetProperty("bankAccountId").GetGuid(),
                    b.GetProperty("expectedBook").GetDecimal(),
                    b.TryGetProperty("accountCode", out var ac) && ac.ValueKind == JsonValueKind.String
                        ? ac.GetString()
                        : null));
            }
        }

        return new VerificationRequest(cutoverDate, ownerEquity, depositLiability, banks);
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

    /// <summary>The result of recomputing tie-out against the current journal.</summary>
    private sealed record TieOutResult(
        MigrationVerificationData Data,
        List<VarianceLine> Lines,
        decimal VarianceTotal,
        bool IsTied);
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
