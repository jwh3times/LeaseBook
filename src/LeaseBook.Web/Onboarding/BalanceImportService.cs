using System.Text.Json;
using LeaseBook.Migrator;
using LeaseBook.Migrator.Csv;
using LeaseBook.Migrator.Model;
using LeaseBook.Migrator.Profiles;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Onboarding.Persistence;
using Microsoft.EntityFrameworkCore;
using DirectoryBankPurpose = LeaseBook.Modules.Directory.Domain.BankPurpose;

namespace LeaseBook.Web.Onboarding;

/// <summary>
/// Orchestrates balance import for one CSV upload (WP-3 Task 3.2). Parses the CSV via
/// the WP-1 binders for the given kind, resolves external ids → LeaseBook ids via prior entity-import
/// rows and bank account name matching, then posts one
/// <see cref="IBalanceForward.PostOpeningPositionAsync"/> per valid row — all in one ambient
/// RLS transaction. Each row posts into the real account + a <c>migration_clearing</c> contra so the
/// set is self-balancing; a non-tying import simply leaves a clearing residual (WP-4 verification,
/// not this task, blocks go-live on non-zero residuals).
/// </summary>
public sealed class BalanceImportService(
    DbContext db,
    IBalanceForward balanceForward,
    IActorContext actor,
    ExternalIdResolver resolver)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ImportBatchResult> ImportAsync(
        EntityKind kind,
        string mappingProfile,
        string filename,
        DateOnly cutoverDate,
        Stream csvStream,
        CancellationToken ct)
    {
        var profile = AppFolioProfiles.For(kind);
        var rowOutcomes = new List<BalanceRowOutcome>();

        switch (kind)
        {
            case EntityKind.BankBalances:
                await ImportBankBalancesAsync(EntityImporter.ReadBankBalances(csvStream, profile), cutoverDate, rowOutcomes, ct);
                break;
            case EntityKind.OwnerBalances:
                await ImportOwnerBalancesAsync(EntityImporter.ReadOwnerBalances(csvStream, profile), cutoverDate, rowOutcomes, ct);
                break;
            case EntityKind.DepositLiabilities:
                await ImportDepositLiabilitiesAsync(EntityImporter.ReadDepositLiabilities(csvStream, profile), cutoverDate, rowOutcomes, ct);
                break;
            case EntityKind.TenantReceivables:
                await ImportTenantReceivablesAsync(EntityImporter.ReadTenantReceivables(csvStream, profile), cutoverDate, rowOutcomes, ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a balance kind.");
        }

        var totalErrors = rowOutcomes.Count(r => r.IsError);
        var batchErrors = rowOutcomes
            .Where(r => r.IsError)
            .Select(r => new ImportBatchError(r.RowNumber, r.ErrorField!, r.ErrorReason!))
            .ToList();

        // Persist batch + rows in one SaveChanges (within the ambient RLS transaction).
        var batch = ImportBatch.Create(
            kind.ToString(),
            mappingProfile,
            filename,
            rowCount: rowOutcomes.Count,
            errorCount: totalErrors,
            status: totalErrors == 0 ? "posted" : "posted_with_errors",
            actor: actor.UserId);

        db.Set<ImportBatch>().Add(batch);

        foreach (var outcome in rowOutcomes)
        {
            var mappedJson = outcome.IsError
                ? JsonSerializer.Serialize(new { outcome.ExternalId }, JsonOpts)
                : JsonSerializer.Serialize(new { outcome.ExternalId, resultingJournalEntryId = outcome.JournalEntryId }, JsonOpts);

            var errorsJson = outcome.IsError
                ? JsonSerializer.Serialize(
                    new[] { new { field = outcome.ErrorField, reason = outcome.ErrorReason } }, JsonOpts)
                : null;

            var rowStatus = outcome.IsError ? "error"
                : outcome.IsSkipped ? "skipped"
                : outcome.AlreadyPosted ? "already-posted"
                : "posted";

            db.Set<ImportRow>().Add(ImportRow.Create(
                batch.Id,
                outcome.RowNumber,
                outcome.RawJson,
                mappedJson,
                rowStatus,
                errorsJson,
                outcome.JournalEntryId));
        }

        await db.SaveChangesAsync(ct);

        var counts = new ImportOutcomeCounts(
            Posted: rowOutcomes.Count(r => !r.IsError && !r.IsSkipped && !r.AlreadyPosted),
            AlreadyPosted: rowOutcomes.Count(r => r.AlreadyPosted),
            Unchanged: 0,
            Superseded: 0,
            Skipped: rowOutcomes.Count(r => r.IsSkipped),
            Errors: totalErrors);

        return new ImportBatchResult(batch.Id, rowOutcomes.Count, totalErrors, counts, batchErrors);
    }

    // -------------------------------------------------------------------------
    // Per-kind import methods
    // -------------------------------------------------------------------------

    /// <summary>
    /// bank_balances: resolve the bank by name-match (case-insensitive) against org bank accounts;
    /// post debit-normal to TrustBank or PmOperatingBank. Negative balance flips to Credit side.
    /// SourceRef: "opening:{cutover}:bank={bankId}".
    /// </summary>
    private async Task ImportBankBalancesAsync(
        ImportResult<BankBalanceRow> parsed,
        DateOnly cutover,
        List<BalanceRowOutcome> outcomes,
        CancellationToken ct)
    {
        AddParseErrorOutcomes(parsed.Errors, outcomes);

        // Load all active bank accounts for this org (RLS scopes to current org).
        var bankAccounts = await db.Set<BankAccount>()
            .AsNoTracking()
            .Where(b => b.IsActive)
            .ToListAsync(ct);

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalBankId, row.Name, row.BookBalance });

            // Case-insensitive name match. A name must match exactly one active account — two same-named
            // banks would misroute a trust opening balance, so an ambiguous match is a row error, not a guess.
            var matches = bankAccounts
                .Where(b => string.Equals(b.Name, row.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                outcomes.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalBankId, rawJson,
                    "name", $"No bank account named '{row.Name}' found in this org"));
                continue;
            }

            if (matches.Count > 1)
            {
                outcomes.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalBankId, rawJson,
                    "name", $"ambiguous_bank_name: {matches.Count} active bank accounts named '{row.Name}' — cannot route opening balance"));
                continue;
            }

            var bank = matches[0];
            var accountCode = bank.Purpose is DirectoryBankPurpose.Trust or DirectoryBankPurpose.Deposit
                ? AccountCodes.TrustBank(bank.Id)
                : AccountCodes.PmOperatingBank(bank.Id);

            var sourceRef = $"opening:{cutover:yyyy-MM-dd}:bank={bank.Id}";

            // bank is debit-normal; figure of exactly $0.00 → skipped (no-op).
            var result = await PostLineAsync(
                row.BookBalance, debitNormal: true, accountCode, EntryBasis.Both,
                cutover, sourceRef, ownerId: null, tenantId: null, bankAccountId: bank.Id, ct);

            outcomes.Add(result.Posted
                ? BalanceRowOutcome.Success(rowNumber, row.ExternalBankId, rawJson, result.EntryId, result.AlreadyPosted)
                : BalanceRowOutcome.Skipped(rowNumber, row.ExternalBankId, rawJson));
        }
    }

    /// <summary>
    /// owner_balances: resolve ownerId and the operating trust bank (Trust-purpose). Post a cash
    /// line (Basis=Both, credit-normal) and optionally a second accrual-delta line (Basis=Accrual)
    /// when accrual ≠ cash. Negative balance flips to debit side.
    /// SourceRefs: "opening:{cutover}:owner-equity={ownerId}" + "…:owner-equity-accrual={ownerId}".
    /// </summary>
    private async Task ImportOwnerBalancesAsync(
        ImportResult<OwnerBalanceRow> parsed,
        DateOnly cutover,
        List<BalanceRowOutcome> outcomes,
        CancellationToken ct)
    {
        AddParseErrorOutcomes(parsed.Errors, outcomes);

        var ownerMap = await resolver.BuildMapAsync(EntityKind.Owners, ct);
        var operatingTrustId = await ResolveOperatingTrustAsync(ct);

        if (operatingTrustId is null)
        {
            foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
            {
                var rawJson = SerializeRaw(new { row.ExternalOwnerId, row.Name, row.CashBalance, row.AccrualBalance });
                outcomes.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalOwnerId, rawJson,
                    "bank", "No operating trust bank account (Trust purpose) found in this org"));
            }
            return;
        }

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalOwnerId, row.Name, row.CashBalance, row.AccrualBalance });

            if (!ownerMap.TryGetValue(row.ExternalOwnerId, out var ownerId))
            {
                outcomes.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalOwnerId, rawJson,
                    "external_owner_id",
                    $"'{row.ExternalOwnerId}' not found in imported owners"));
                continue;
            }

            // Two independent lines per row (Fix 1): the cash line (Basis=Both) and the accrual-delta
            // line (Basis=Accrual). Each is evaluated and skipped independently when its figure is $0.00,
            // so cash=0/accrual=200 still posts the $200 accrual delta. owner_equity is credit-normal.
            var cashSourceRef = $"opening:{cutover:yyyy-MM-dd}:owner-equity={ownerId}";
            var cashResult = await PostLineAsync(
                row.CashBalance, debitNormal: false, AccountCodes.OwnerEquity, EntryBasis.Both,
                cutover, cashSourceRef, ownerId: ownerId, tenantId: null, bankAccountId: operatingTrustId, ct);

            // Accrual-delta line: posts the (accrual − cash) delta tagged Accrual, distinct source_ref.
            // A delta of 0 (accrual == cash) is a no-op.
            var delta = row.AccrualBalance - row.CashBalance;
            var accrualSourceRef = $"opening:{cutover:yyyy-MM-dd}:owner-equity-accrual={ownerId}";
            var accrualResult = await PostLineAsync(
                delta, debitNormal: false, AccountCodes.OwnerEquity, EntryBasis.Accrual,
                cutover, accrualSourceRef, ownerId: ownerId, tenantId: null, bankAccountId: operatingTrustId, ct);

            // The row's recorded outcome tracks the cash line (the primary opening position). If the cash
            // line was a no-op but the accrual delta posted, surface the accrual entry id so the row is
            // recorded as posted, not skipped. Only when NEITHER line posted is the row a no-op skip.
            if (cashResult.Posted)
            {
                outcomes.Add(BalanceRowOutcome.Success(rowNumber, row.ExternalOwnerId, rawJson,
                    cashResult.EntryId, cashResult.AlreadyPosted));
            }
            else if (accrualResult.Posted)
            {
                outcomes.Add(BalanceRowOutcome.Success(rowNumber, row.ExternalOwnerId, rawJson,
                    accrualResult.EntryId, accrualResult.AlreadyPosted));
            }
            else
            {
                outcomes.Add(BalanceRowOutcome.Skipped(rowNumber, row.ExternalOwnerId, rawJson));
            }
        }
    }

    /// <summary>
    /// deposit_liabilities: resolve tenantId and ownerId; post to SecurityDepositsHeld
    /// (credit-normal, Basis=Both) against the deposit trust bank.
    /// SourceRef: "opening:{cutover}:deposit={tenantId}".
    /// </summary>
    private async Task ImportDepositLiabilitiesAsync(
        ImportResult<DepositLiabilityRow> parsed,
        DateOnly cutover,
        List<BalanceRowOutcome> outcomes,
        CancellationToken ct)
    {
        AddParseErrorOutcomes(parsed.Errors, outcomes);

        var ownerMap = await resolver.BuildMapAsync(EntityKind.Owners, ct);
        var tenantMap = await BuildTenantMapAsync(ct);
        var depositTrustId = await ResolveDepositTrustAsync(ct);

        if (depositTrustId is null)
        {
            foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
            {
                var rawJson = SerializeRaw(new { row.ExternalTenantId, row.ExternalOwnerId, row.HeldAmount });
                outcomes.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalTenantId, rawJson,
                    "bank", "No deposit trust bank account (Deposit purpose) found in this org"));
            }
            return;
        }

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalTenantId, row.ExternalOwnerId, row.HeldAmount });

            if (!tenantMap.TryGetValue(row.ExternalTenantId, out var tenantId))
            {
                outcomes.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalTenantId, rawJson,
                    "external_tenant_id",
                    $"'{row.ExternalTenantId}' not found in imported tenants"));
                continue;
            }

            if (!ownerMap.TryGetValue(row.ExternalOwnerId, out var ownerId))
            {
                outcomes.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalTenantId, rawJson,
                    "external_owner_id",
                    $"'{row.ExternalOwnerId}' not found in imported owners"));
                continue;
            }

            // SecurityDepositsHeld is credit-normal; a held amount of exactly $0.00 → skipped (no-op).
            var sourceRef = $"opening:{cutover:yyyy-MM-dd}:deposit={tenantId}";
            var result = await PostLineAsync(
                row.HeldAmount, debitNormal: false, AccountCodes.SecurityDepositsHeld, EntryBasis.Both,
                cutover, sourceRef, ownerId: ownerId, tenantId: tenantId, bankAccountId: depositTrustId, ct);

            outcomes.Add(result.Posted
                ? BalanceRowOutcome.Success(rowNumber, row.ExternalTenantId, rawJson, result.EntryId, result.AlreadyPosted)
                : BalanceRowOutcome.Skipped(rowNumber, row.ExternalTenantId, rawJson));
        }
    }

    /// <summary>
    /// tenant_receivables: resolve tenantId and ownerId; post to TenantReceivable
    /// (debit-normal, Basis=Accrual). No bank dimension.
    /// SourceRef: "opening:{cutover}:receivable={tenantId}".
    /// </summary>
    private async Task ImportTenantReceivablesAsync(
        ImportResult<TenantReceivableRow> parsed,
        DateOnly cutover,
        List<BalanceRowOutcome> outcomes,
        CancellationToken ct)
    {
        AddParseErrorOutcomes(parsed.Errors, outcomes);

        var ownerMap = await resolver.BuildMapAsync(EntityKind.Owners, ct);
        var tenantMap = await BuildTenantMapAsync(ct);

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalTenantId, row.ExternalOwnerId, row.Balance });

            if (!tenantMap.TryGetValue(row.ExternalTenantId, out var tenantId))
            {
                outcomes.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalTenantId, rawJson,
                    "external_tenant_id",
                    $"'{row.ExternalTenantId}' not found in imported tenants"));
                continue;
            }

            if (!ownerMap.TryGetValue(row.ExternalOwnerId, out var ownerId))
            {
                outcomes.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalTenantId, rawJson,
                    "external_owner_id",
                    $"'{row.ExternalOwnerId}' not found in imported owners"));
                continue;
            }

            // TenantReceivable is debit-normal; a balance of exactly $0.00 → skipped (no-op). No bank dim.
            var sourceRef = $"opening:{cutover:yyyy-MM-dd}:receivable={tenantId}";
            var result = await PostLineAsync(
                row.Balance, debitNormal: true, AccountCodes.TenantReceivable, EntryBasis.Accrual,
                cutover, sourceRef, ownerId: ownerId, tenantId: tenantId, bankAccountId: null, ct);

            outcomes.Add(result.Posted
                ? BalanceRowOutcome.Success(rowNumber, row.ExternalTenantId, rawJson, result.EntryId, result.AlreadyPosted)
                : BalanceRowOutcome.Skipped(rowNumber, row.ExternalTenantId, rawJson));
        }
    }

    // -------------------------------------------------------------------------
    // Bank resolution helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the id of the first (oldest) active Trust-purpose bank account, or null if none.
    /// Used for owner_balances (the "operating trust" is Trust-purpose).
    /// </summary>
    private async Task<Guid?> ResolveOperatingTrustAsync(CancellationToken ct) =>
        (await db.Set<BankAccount>()
            .AsNoTracking()
            .Where(b => b.IsActive && b.Purpose == DirectoryBankPurpose.Trust)
            .OrderBy(b => b.CreatedAt)
            .FirstOrDefaultAsync(ct))?.Id;

    /// <summary>
    /// Returns the id of the first (oldest) active Deposit-purpose bank account, or null if none.
    /// Used for deposit_liabilities.
    /// </summary>
    private async Task<Guid?> ResolveDepositTrustAsync(CancellationToken ct) =>
        (await db.Set<BankAccount>()
            .AsNoTracking()
            .Where(b => b.IsActive && b.Purpose == DirectoryBankPurpose.Deposit)
            .OrderBy(b => b.CreatedAt)
            .FirstOrDefaultAsync(ct))?.Id;

    // -------------------------------------------------------------------------
    // Tenant id resolution
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a map of external tenant id → LeaseBook tenant id (not the lease id).
    /// TenantsLeases import rows store <c>{ externalId, leaseBookId (=leaseId), tenantId }</c>
    /// in MappedJson; we need the <c>tenantId</c> field because balance dimensions reference
    /// the tenant entity, not the lease.
    /// </summary>
    private async Task<Dictionary<string, Guid>> BuildTenantMapAsync(CancellationToken ct)
    {
        var kindStr = EntityKind.TenantsLeases.ToString();

        var mappedJsonRows = await db.Set<ImportBatch>()
            .Where(b => b.EntityKind == kindStr
                        && (b.Status == "posted" || b.Status == "posted_with_errors"))
            .Join(db.Set<ImportRow>(),
                b => b.Id,
                r => r.BatchId,
                (_, r) => r)
            .Where(r => r.RowStatus == "posted")
            .Select(r => r.MappedJson)
            .ToListAsync(ct);

        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var json in mappedJsonRows)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("externalId", out var extEl) &&
                    doc.RootElement.TryGetProperty("tenantId", out var tEl) &&
                    extEl.ValueKind == JsonValueKind.String &&
                    tEl.TryGetGuid(out var tenantId))
                {
                    result[extEl.GetString()!] = tenantId;
                }
            }
            catch (JsonException)
            {
                // Corrupt mapped_json — skip silently.
            }
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Shared utilities (mirrors EntityImportService pattern)
    // -------------------------------------------------------------------------

    private static void AddParseErrorOutcomes(IReadOnlyList<RowError> parseErrors, List<BalanceRowOutcome> outcomes)
    {
        foreach (var e in parseErrors)
            outcomes.Add(BalanceRowOutcome.Error(e.RowNumber, string.Empty, "{}", e.Field, e.Reason));
    }

    /// <summary>
    /// Pairs each valid row with its true 1-based source CSV row number, skipping slots taken
    /// by parse errors (same interleaving logic as <see cref="EntityImportService"/>).
    /// </summary>
    private static IEnumerable<(TRow Row, int RowNumber)> WithSourceRowNumbers<TRow>(
        IReadOnlyList<TRow> validRows,
        IReadOnlyList<RowError> parseErrors)
    {
        var errorRowNumbers = parseErrors.Select(e => e.RowNumber).ToHashSet();
        var sourceRow = 0;
        foreach (var row in validRows)
        {
            do { sourceRow++; } while (errorRowNumbers.Contains(sourceRow));
            yield return (row, sourceRow);
        }
    }

    private static string SerializeRaw(object obj) =>
        JsonSerializer.Serialize(obj, JsonOpts);

    /// <summary>
    /// The outcome of attempting to post one opening line: whether it actually posted (a
    /// strictly-positive figure), the resulting entry id, and whether it was an idempotent re-post.
    /// A <see cref="Posted"/>=<c>false</c> result means the figure was exactly $0.00 — a no-op,
    /// never sent to the posting service (which rejects non-positive amounts).
    /// </summary>
    private readonly record struct LineResult(bool Posted, Guid EntryId, bool AlreadyPosted);

    /// <summary>
    /// Posts ONE opening line via <see cref="IBalanceForward.PostOpeningPositionAsync"/>, after
    /// mapping a signed <paramref name="figure"/> onto the account's normal side.
    /// A figure of exactly $0.00 is skipped (no-op) — <see cref="PostingService"/> rejects
    /// non-positive amounts, and a zero opening position carries no information. A
    /// <see cref="DuplicateSourceRefException"/> is caught and surfaced as an idempotent re-post.
    /// </summary>
    /// <param name="debitNormal">
    /// True when the account is debit-normal (bank, receivable): a positive figure → Debit.
    /// False when credit-normal (owner equity, deposits): a positive figure → Credit.
    /// A negative figure flips to the opposite side.
    /// </param>
    private async Task<LineResult> PostLineAsync(
        decimal figure,
        bool debitNormal,
        string accountCode,
        EntryBasis basis,
        DateOnly cutover,
        string sourceRef,
        Guid? ownerId,
        Guid? tenantId,
        Guid? bankAccountId,
        CancellationToken ct)
    {
        // Exactly zero → no-op. Never call the posting service (Money(0) would throw InvalidLineException).
        if (figure == 0m)
        {
            return new LineResult(Posted: false, EntryId: default, AlreadyPosted: false);
        }

        // Map the signed figure onto the account's normal side; a negative figure flips it.
        var positiveSide = figure > 0m;
        var onDebit = debitNormal ? positiveSide : !positiveSide;
        var amount = new Money(Math.Abs(figure));
        Money? debit = onDebit ? amount : null;
        Money? credit = onDebit ? null : amount;

        try
        {
            var entryId = await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                accountCode, debit, credit, basis,
                cutover, sourceRef,
                OwnerId: ownerId, TenantId: tenantId, BankAccountId: bankAccountId), ct);
            return new LineResult(Posted: true, EntryId: entryId, AlreadyPosted: false);
        }
        catch (DuplicateSourceRefException ex)
        {
            return new LineResult(Posted: true, EntryId: ex.ExistingEntryId, AlreadyPosted: true);
        }
    }

    // -------------------------------------------------------------------------
    // Private outcome record
    // -------------------------------------------------------------------------

    private sealed record BalanceRowOutcome(
        int RowNumber,
        string ExternalId,
        string RawJson,
        Guid? JournalEntryId,
        bool AlreadyPosted,
        bool IsSkipped,
        bool IsError,
        string? ErrorField,
        string? ErrorReason)
    {
        public static BalanceRowOutcome Success(int rowNumber, string externalId, string rawJson, Guid entryId, bool alreadyPosted) =>
            new(rowNumber, externalId, rawJson, entryId, alreadyPosted, false, false, null, null);

        /// <summary>A no-op row: every line of the row was an exactly-zero figure, so nothing was posted.</summary>
        public static BalanceRowOutcome Skipped(int rowNumber, string externalId, string rawJson) =>
            new(rowNumber, externalId, rawJson, null, false, true, false, null, null);

        public static BalanceRowOutcome Error(int rowNumber, string externalId, string rawJson, string field, string reason) =>
            new(rowNumber, externalId, rawJson, null, false, false, true, field, reason);
    }
}
