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

        return new ImportBatchResult(batch.Id, rowOutcomes.Count, totalErrors, batchErrors);
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

            // Case-insensitive name match
            var bank = bankAccounts.FirstOrDefault(b =>
                string.Equals(b.Name, row.Name, StringComparison.OrdinalIgnoreCase));

            if (bank is null)
            {
                outcomes.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalBankId, rawJson,
                    "name", $"No bank account named '{row.Name}' found in this org"));
                continue;
            }

            var accountCode = bank.Purpose is DirectoryBankPurpose.Trust or DirectoryBankPurpose.Deposit
                ? AccountCodes.TrustBank(bank.Id)
                : AccountCodes.PmOperatingBank(bank.Id);

            // bank is debit-normal; negative balance flips to credit side
            Money? debit, credit;
            if (row.BookBalance >= 0)
            {
                debit = new Money(row.BookBalance);
                credit = null;
            }
            else
            {
                debit = null;
                credit = new Money(-row.BookBalance);
            }

            var sourceRef = $"opening:{cutover:yyyy-MM-dd}:bank={bank.Id}";

            Guid entryId;
            bool alreadyPosted;
            try
            {
                entryId = await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                    accountCode, debit, credit, EntryBasis.Both,
                    cutover, sourceRef,
                    BankAccountId: bank.Id), ct);
                alreadyPosted = false;
            }
            catch (DuplicateSourceRefException ex)
            {
                entryId = ex.ExistingEntryId;
                alreadyPosted = true;
            }

            outcomes.Add(BalanceRowOutcome.Success(rowNumber, row.ExternalBankId, rawJson, entryId, alreadyPosted));
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

            // Cash line (Basis=Both) — owner_equity is credit-normal; negative flips to debit
            Money? cashDebit, cashCredit;
            if (row.CashBalance >= 0)
            {
                cashDebit = null;
                cashCredit = new Money(row.CashBalance);
            }
            else
            {
                cashDebit = new Money(-row.CashBalance);
                cashCredit = null;
            }

            var cashSourceRef = $"opening:{cutover:yyyy-MM-dd}:owner-equity={ownerId}";

            Guid cashEntryId;
            bool cashAlreadyPosted;
            try
            {
                cashEntryId = await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                    AccountCodes.OwnerEquity, cashDebit, cashCredit, EntryBasis.Both,
                    cutover, cashSourceRef,
                    OwnerId: ownerId, BankAccountId: operatingTrustId), ct);
                cashAlreadyPosted = false;
            }
            catch (DuplicateSourceRefException ex)
            {
                cashEntryId = ex.ExistingEntryId;
                cashAlreadyPosted = true;
            }

            outcomes.Add(BalanceRowOutcome.Success(rowNumber, row.ExternalOwnerId, rawJson, cashEntryId, cashAlreadyPosted));

            // Accrual-delta line (Basis=Accrual): only when accrual ≠ cash. This is a second journal
            // entry for the same CSV row; we catch DuplicateSourceRefException for idempotency.
            // The batch RowCount stays equal to CSV rows (no extra outcome added for the delta line).
            if (row.AccrualBalance != row.CashBalance)
            {
                var delta = row.AccrualBalance - row.CashBalance;
                Money? deltaDebit, deltaCredit;
                if (delta >= 0)
                {
                    deltaDebit = null;
                    deltaCredit = new Money(delta);
                }
                else
                {
                    deltaDebit = new Money(-delta);
                    deltaCredit = null;
                }

                var accrualSourceRef = $"opening:{cutover:yyyy-MM-dd}:owner-equity-accrual={ownerId}";

                try
                {
                    await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                        AccountCodes.OwnerEquity, deltaDebit, deltaCredit, EntryBasis.Accrual,
                        cutover, accrualSourceRef,
                        OwnerId: ownerId, BankAccountId: operatingTrustId), ct);
                }
                catch (DuplicateSourceRefException)
                {
                    // Already posted on a prior run — idempotent, do nothing.
                }
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

            // SecurityDepositsHeld is credit-normal; held amount should always be positive
            Money? debit, credit;
            if (row.HeldAmount >= 0)
            {
                debit = null;
                credit = new Money(row.HeldAmount);
            }
            else
            {
                debit = new Money(-row.HeldAmount);
                credit = null;
            }

            var sourceRef = $"opening:{cutover:yyyy-MM-dd}:deposit={tenantId}";

            Guid entryId;
            bool alreadyPosted;
            try
            {
                entryId = await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                    AccountCodes.SecurityDepositsHeld, debit, credit, EntryBasis.Both,
                    cutover, sourceRef,
                    TenantId: tenantId, OwnerId: ownerId, BankAccountId: depositTrustId), ct);
                alreadyPosted = false;
            }
            catch (DuplicateSourceRefException ex)
            {
                entryId = ex.ExistingEntryId;
                alreadyPosted = true;
            }

            outcomes.Add(BalanceRowOutcome.Success(rowNumber, row.ExternalTenantId, rawJson, entryId, alreadyPosted));
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

            // TenantReceivable is debit-normal; negative balance flips to credit
            Money? debit, credit;
            if (row.Balance >= 0)
            {
                debit = new Money(row.Balance);
                credit = null;
            }
            else
            {
                debit = null;
                credit = new Money(-row.Balance);
            }

            var sourceRef = $"opening:{cutover:yyyy-MM-dd}:receivable={tenantId}";

            Guid entryId;
            bool alreadyPosted;
            try
            {
                entryId = await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                    AccountCodes.TenantReceivable, debit, credit, EntryBasis.Accrual,
                    cutover, sourceRef,
                    TenantId: tenantId, OwnerId: ownerId), ct);
                alreadyPosted = false;
            }
            catch (DuplicateSourceRefException ex)
            {
                entryId = ex.ExistingEntryId;
                alreadyPosted = true;
            }

            outcomes.Add(BalanceRowOutcome.Success(rowNumber, row.ExternalTenantId, rawJson, entryId, alreadyPosted));
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

    // -------------------------------------------------------------------------
    // Private outcome record
    // -------------------------------------------------------------------------

    private sealed record BalanceRowOutcome(
        int RowNumber,
        string ExternalId,
        string RawJson,
        Guid? JournalEntryId,
        bool AlreadyPosted,
        bool IsError,
        string? ErrorField,
        string? ErrorReason)
    {
        public static BalanceRowOutcome Success(int rowNumber, string externalId, string rawJson, Guid entryId, bool alreadyPosted) =>
            new(rowNumber, externalId, rawJson, entryId, alreadyPosted, false, null, null);

        public static BalanceRowOutcome Error(int rowNumber, string externalId, string rawJson, string field, string reason) =>
            new(rowNumber, externalId, rawJson, null, false, true, field, reason);
    }
}
