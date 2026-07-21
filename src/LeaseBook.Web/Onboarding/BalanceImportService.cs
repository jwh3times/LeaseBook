using System.Text.Json;
using LeaseBook.Migrator;
using LeaseBook.Migrator.Csv;
using LeaseBook.Migrator.Model;
using LeaseBook.Migrator.Profiles;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Migration;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Observability;
using LeaseBook.Web.Onboarding.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DirectoryBankPurpose = LeaseBook.Modules.Directory.Domain.BankPurpose;

namespace LeaseBook.Web.Onboarding;

/// <summary>
/// Orchestrates balance import for one CSV upload (WP-3 Task 3.2). Parses the CSV via
/// the WP-1 binders for the given kind, resolves external ids → LeaseBook ids via prior entity-import
/// rows and bank account name matching, then posts one
/// <see cref="IBalanceForward.PostOpeningPositionAsync"/> per valid row — all in one ambient
/// RLS transaction. Each row posts into the real account + a <c>migration_clearing</c> contra so the
/// set is self-balancing; a non-tying import simply leaves a clearing residual (WP-4 verification,
/// not this task, blocks go-live on non-zero residuals). Row resolution (CSV row → account/dims) is
/// split out into <see cref="PlanAsync"/> so the WP-7 supersede engine can reuse the same resolution
/// logic without re-implementing it against posting.
/// </summary>
public sealed class BalanceImportService(
    DbContext db,
    IBalanceForward balanceForward,
    IActorContext actor,
    ExternalIdResolver resolver,
    ISender sender,
    IReversalService reversal,
    ILogger<BalanceImportService> logger)
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
        var plan = await PlanAsync(kind, cutoverDate, csvStream, ct);
        var rowOutcomes = new List<BalanceRowOutcome>(plan.ResolutionErrors);

        foreach (var group in plan.Positions.GroupBy(p => p.RowNumber))
        {
            var lines = new List<(PlannedPosition Position, LineResult Result)>();
            var rowRejected = false;
            foreach (var p in group)
            {
                try
                {
                    var result = await PostLineAsync(
                        p.Figure, p.DebitNormal, p.AccountCode, p.Basis,
                        cutoverDate, p.SourceRef, p.OwnerId, p.TenantId, p.BankAccountId, ct);
                    lines.Add((p, result));
                }
                catch (InvalidOpeningPositionException ex)
                {
                    // WP-7 §3.1: a pm_income opening whose shape violates the held-fees invariant
                    // (checked at post time, not plan time — Task 9). S2-clean message to the row;
                    // the code + source_ref are the technical detail, logged not surfaced.
                    logger.LogWarning(LogEvents.HeldFeesShapeRejected,
                        "Held-fees opening rejected ({Code}) for source_ref {SourceRef}", ex.Code, p.SourceRef);
                    rowOutcomes.Add(BalanceRowOutcome.Error(p.RowNumber, p.ExternalId, p.RawJson, "held_fees", ex.Message));
                    rowRejected = true;
                    break;
                }
            }

            if (rowRejected)
            {
                continue;
            }

            var head = lines[0].Position;

            if (kind == EntityKind.OwnerBalances)
            {
                // owner_balances plans two independent positions per row, in this order: the cash
                // position (Basis=Both) then the accrual-delta position (Basis=Accrual) — see
                // PlanOwnerBalancesAsync. The row's recorded outcome tracks the cash line (the
                // primary opening position). If the cash line was a no-op but the accrual delta
                // posted, the accrual entry id surfaces instead so the row is recorded as posted,
                // not skipped. Only when NEITHER line posted is the row a no-op skip.
                var (_, cashResult) = lines[0];
                var (_, accrualResult) = lines[1];

                if (cashResult.Posted)
                {
                    rowOutcomes.Add(BalanceRowOutcome.Success(group.Key, head.ExternalId, head.RawJson,
                        cashResult.EntryId, cashResult.AlreadyPosted));
                }
                else if (accrualResult.Posted)
                {
                    rowOutcomes.Add(BalanceRowOutcome.Success(group.Key, head.ExternalId, head.RawJson,
                        accrualResult.EntryId, accrualResult.AlreadyPosted));
                }
                else
                {
                    rowOutcomes.Add(BalanceRowOutcome.Skipped(group.Key, head.ExternalId, head.RawJson));
                }
            }
            else
            {
                var (_, result) = lines[0];
                rowOutcomes.Add(result.Posted
                    ? BalanceRowOutcome.Success(group.Key, head.ExternalId, head.RawJson, result.EntryId, result.AlreadyPosted)
                    : BalanceRowOutcome.Skipped(group.Key, head.ExternalId, head.RawJson));
            }
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
        PersistRows(batch.Id, rowOutcomes);

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
    // Supersede: pre-sign-off corrected re-import (WP-7 Task 5, design §2).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pre-sign-off corrected re-import (WP-7 Half A, design §2). File-scoped: only positions present
    /// in the corrected file are considered — an omitted family is left as-is (omission ≠ removal, D8).
    /// Per position, diffed against the live opening entry of its base source_ref family: an identical
    /// figure is left untouched (S3 idempotency by figure comparison); a changed figure posts a linked
    /// reversal dated at <paramref name="cutoverDate"/> (never today, R1) then a corrected revision at
    /// the next <c>#r{N}</c>; a correction to $0.00 posts the reversal only. Everything runs on the
    /// ambient RLS transaction with one terminal <c>SaveChanges</c> — a mid-way throw rolls back whole.
    /// The three §2 guards throw <see cref="SupersedeConflictException"/> before any write, so a blocked
    /// path leaves no batch, audit, or journal row.
    /// </summary>
    public async Task<ImportBatchResult> SupersedeAsync(
        EntityKind kind,
        string mappingProfile,
        string filename,
        DateOnly cutoverDate,
        Stream csvStream,
        CancellationToken ct)
    {
        // Guard 1 (§2.5): a signed-off migration is closed to import machinery — corrections are ledger
        // reversals now, not re-imports.
        if (await db.Set<MigrationVerification>().AnyAsync(v => v.SignedOffAt != null, ct))
        {
            throw new SupersedeConflictException("already_signed_off",
                "This migration is already signed off. Correct figures with a ledger reversal instead.");
        }

        // Guard 2 (§2.9): a corrected re-import needs a prior import of this balance type to correct.
        var priorStatuses = new[] { "posted", "posted_with_errors" };
        var priorBatch = await db.Set<ImportBatch>()
            .Where(b => b.EntityKind == kind.ToString() && priorStatuses.Contains(b.Status))
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (priorBatch is null)
        {
            throw new SupersedeConflictException("nothing_to_supersede",
                "No prior import of this balance type exists. Use the ordinary import instead.");
        }

        // The live opening positions (event_type='OpeningBalance', real leg, IsReversed flag) — the
        // #r{N} count is derived from THIS journal read, never from the request (R3). Voids never appear.
        var live = await sender.Query(new GetOpeningPositions(), ct);

        // Guard 3 (§2.9): the base refs embed the cutover date; a different date would match no family
        // and double-post every position as new. The date is read from the journal, never trusted.
        var existingDates = live.Entries
            .Select(e => e.SourceRef.Split(':'))
            .Where(parts => parts.Length >= 3 && parts[0] == "opening")
            .Select(parts => parts[1])
            .Distinct()
            .ToList();
        var requestDate = cutoverDate.ToString("yyyy-MM-dd");
        if (existingDates.Count > 0 && !existingDates.Contains(requestDate))
        {
            throw new SupersedeConflictException("cutover_date_mismatch",
                $"The corrected file's cutover date ({requestDate}) does not match the imported cutover date ({existingDates[0]}). Changing the cutover date requires re-provisioning.");
        }

        // Family map keyed by base ref (strip a trailing #r{N}); the live member is the unreversed entry.
        // Snapshotted before any reversal, so 1 + family.Count is the pre-supersede revision count.
        var families = live.Entries
            .GroupBy(e => BaseRef(e.SourceRef))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var plan = await PlanAsync(kind, cutoverDate, csvStream, ct);
        var outcomes = new List<BalanceRowOutcome>(plan.ResolutionErrors);

        // Row buckets partition every resolvable row exactly once, by precedence
        // Superseded > Posted > Unchanged > Skipped (error rows are the pre-seeded ResolutionErrors).
        var posted = 0;
        var unchanged = 0;
        var superseded = 0;
        var skipped = 0;
        var reversedEntryIds = new List<Guid>();

        foreach (var rowGroup in plan.Positions.GroupBy(p => p.RowNumber))
        {
            var head = rowGroup.First();
            Guid? revisionEntryId = null;  // a newly posted corrected revision or brand-new position
            Guid? reversalEntryId = null;  // a void mirror (the only journal effect of a $0 correction)
            Guid? unchangedEntryId = null; // an untouched live entry (idempotent row)
            var anyChanged = false;
            var anyNewPost = false;
            var anyUnchanged = false;
            var rowRejected = false;

            // A row groups one-or-more positions (owner rows plan two: cash then accrual-delta); each is
            // diffed against its own family independently, so this stays cardinality-agnostic.
            foreach (var p in rowGroup)
            {
                var family = families.GetValueOrDefault(p.SourceRef, []);
                var current = family.FirstOrDefault(e => !e.IsReversed);
                var (targetDebit, targetCredit) = MapToSides(p.Figure, p.DebitNormal);

                // Unchanged: a live entry exists and already sits on the corrected side/amount (S3).
                if (current is not null && current.Debit == targetDebit && current.Credit == targetCredit)
                {
                    anyUnchanged = true;
                    unchangedEntryId ??= current.EntryId;
                    continue;
                }

                // Changed family: reverse the live entry FIRST (§2.1), dated at the cutover (§2.2/R1).
                if (current is not null)
                {
                    try
                    {
                        reversalEntryId = await reversal.ReverseAsync(
                            current.EntryId, "Superseded by corrected re-import", cutoverDate,
                            sourceRef: $"{current.SourceRef}:void", ct);
                        reversedEntryIds.Add(current.EntryId);
                    }
                    catch (AlreadyReversedException)
                    {
                        // Concurrency backstop (§2.1/S3): a racing request already reversed it — converge
                        // on success and treat it exactly like our own reversal (never a 409).
                        logger.LogInformation(LogEvents.SupersedeReversalRace,
                            "Supersede reversal race on entry {EntryId}; treating as already reversed",
                            current.EntryId);
                    }

                    anyChanged = true;
                }

                // Repost the corrected figure at the next revision ref; a $0 correction posts nothing.
                if (p.Figure != 0m)
                {
                    var nextRev = 1 + family.Count;                          // journal-derived (§2.3/R3)
                    var newRef = current is null && family.Count == 0
                        ? p.SourceRef                                        // never posted → base ref
                        : $"{p.SourceRef}#r{nextRev}";
                    try
                    {
                        var result = await PostLineAsync(
                            p.Figure, p.DebitNormal, p.AccountCode, p.Basis,
                            cutoverDate, newRef, p.OwnerId, p.TenantId, p.BankAccountId, ct);
                        if (result.Posted)
                        {
                            revisionEntryId ??= result.EntryId;
                            if (current is null)
                            {
                                anyNewPost = true;
                            }
                        }
                    }
                    catch (InvalidOpeningPositionException ex)
                    {
                        // WP-7 §3.1: same post-time held-fees shape guard as ImportAsync (Task 9).
                        // S2-clean message to the row; code + source_ref are the logged technical detail.
                        logger.LogWarning(LogEvents.HeldFeesShapeRejected,
                            "Held-fees opening rejected ({Code}) for source_ref {SourceRef}", ex.Code, p.SourceRef);
                        outcomes.Add(BalanceRowOutcome.Error(p.RowNumber, p.ExternalId, p.RawJson, "held_fees", ex.Message));
                        rowRejected = true;
                        break;
                    }
                }
            }

            if (rowRejected)
            {
                continue;
            }

            // Bucket the row exactly once by precedence, and record one outcome row for it. The resulting
            // journal-entry id prefers a new revision, then the void mirror, then the untouched entry.
            bool alreadyPosted;
            if (anyChanged) { superseded++; alreadyPosted = false; }
            else if (anyNewPost) { posted++; alreadyPosted = false; }
            else if (anyUnchanged) { unchanged++; alreadyPosted = true; }
            else { skipped++; alreadyPosted = false; }

            var resultingId = revisionEntryId ?? reversalEntryId ?? unchangedEntryId;
            outcomes.Add(resultingId is Guid id
                ? BalanceRowOutcome.Success(head.RowNumber, head.ExternalId, head.RawJson, id, alreadyPosted)
                : BalanceRowOutcome.Skipped(head.RowNumber, head.ExternalId, head.RawJson));
        }

        var totalErrors = outcomes.Count(r => r.IsError);
        var batch = ImportBatch.Create(
            kind.ToString(),
            mappingProfile,
            filename,
            rowCount: outcomes.Count,
            errorCount: totalErrors,
            status: totalErrors == 0 ? "posted" : "posted_with_errors",
            actor: actor.UserId,
            supersedesBatchId: priorBatch.Id);

        db.Set<ImportBatch>().Add(batch);
        PersistRows(batch.Id, outcomes);

        // Explicit domain audit alongside the row-level auto-audit (mirrors VerificationService's pattern).
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            Id = UuidV7.NewId(),
            ActorUserId = actor.UserId,
            EntityType = "import-superseded",
            EntityId = batch.Id,
            Action = "insert",
            Before = null,
            After = JsonSerializer.Serialize(new
            {
                batchId = batch.Id,
                supersedesBatchId = priorBatch.Id,
                kind = kind.ToString(),
                superseded,
                posted,
                unchanged,
                skipped,
                reversedEntryIds,
            }, JsonOpts),
        });

        await db.SaveChangesAsync(ct);

        var counts = new ImportOutcomeCounts(posted, AlreadyPosted: 0, unchanged, superseded, skipped, totalErrors);
        return new ImportBatchResult(batch.Id, outcomes.Count, totalErrors, counts,
            outcomes.Where(r => r.IsError)
                .Select(r => new ImportBatchError(r.RowNumber, r.ErrorField!, r.ErrorReason!))
                .ToList());
    }

    /// <summary>
    /// Strips a trailing <c>#r{N}</c> revision suffix to recover the base source_ref, so the original
    /// entry and every revision of the same position group under one family. Refs never contain
    /// <c>#r</c> otherwise (voids are excluded from the source query, so their <c>:void</c> suffix is
    /// never seen here).
    /// </summary>
    private static string BaseRef(string sourceRef)
    {
        var i = sourceRef.LastIndexOf("#r", StringComparison.Ordinal);
        return i < 0 ? sourceRef : sourceRef[..i];
    }

    /// <summary>
    /// Maps a signed figure onto the (debit, credit) sides an opening line would post — the pure-function
    /// twin of <see cref="PostLineAsync"/>'s side logic, used to compare a corrected figure against a
    /// live entry's real leg. A $0.00 figure maps to (null, null): no side, so it never equals a posted
    /// leg and a live entry corrected to zero always reads as changed.
    /// </summary>
    private static (decimal? Debit, decimal? Credit) MapToSides(decimal figure, bool debitNormal)
    {
        if (figure == 0m)
        {
            return (null, null);
        }

        var onDebit = debitNormal ? figure > 0m : figure < 0m;
        var amount = Math.Abs(figure);
        return onDebit ? (amount, null) : (null, amount);
    }

    /// <summary>
    /// Stages one <see cref="ImportRow"/> per outcome (shared verbatim by <see cref="ImportAsync"/> and
    /// <see cref="SupersedeAsync"/>): error rows carry their field/reason, others carry the resulting
    /// journal-entry id, and the row status derives from the outcome (error / skipped / already-posted /
    /// posted). No SaveChanges — the caller flushes with the batch in one terminal write.
    /// </summary>
    private void PersistRows(Guid batchId, List<BalanceRowOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
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
                batchId,
                outcome.RowNumber,
                outcome.RawJson,
                mappedJson,
                rowStatus,
                errorsJson,
                outcome.JournalEntryId));
        }
    }

    // -------------------------------------------------------------------------
    // Planning: CSV row → resolved account/dims, with no posting (WP-7 Task 4).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses <paramref name="csv"/> for <paramref name="kind"/> and resolves every row to either a
    /// <see cref="BalanceRowOutcome.Error"/> (unresolvable owner/tenant/bank, a missing operating or
    /// deposit trust bank, or a CSV parse error) or one-or-more <see cref="PlannedPosition"/>s — never
    /// both for the same row. Does no posting: <see cref="ImportAsync"/> posts the plan, and the
    /// upcoming supersede engine (WP-7 Task 5) will diff it against prior positions instead.
    /// </summary>
    private Task<BalancePlan> PlanAsync(EntityKind kind, DateOnly cutover, Stream csv, CancellationToken ct)
    {
        var profile = AppFolioProfiles.For(kind);

        return kind switch
        {
            EntityKind.BankBalances =>
                PlanBankBalancesAsync(EntityImporter.ReadBankBalances(csv, profile), cutover, ct),
            EntityKind.OwnerBalances =>
                PlanOwnerBalancesAsync(EntityImporter.ReadOwnerBalances(csv, profile), cutover, ct),
            EntityKind.DepositLiabilities =>
                PlanDepositLiabilitiesAsync(EntityImporter.ReadDepositLiabilities(csv, profile), cutover, ct),
            EntityKind.TenantReceivables =>
                PlanTenantReceivablesAsync(EntityImporter.ReadTenantReceivables(csv, profile), cutover, ct),
            EntityKind.HeldPmFees =>
                PlanHeldPmFeesAsync(EntityImporter.ReadHeldPmFees(csv, profile), cutover, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a balance kind."),
        };
    }

    /// <summary>
    /// bank_balances: resolves the bank by name-match (case-insensitive) against org bank accounts
    /// and plans a debit-normal position against TrustBank or PmOperatingBank (a negative figure
    /// flips to the credit side when the position is posted).
    /// SourceRef: "opening:{cutover}:bank={bankId}".
    /// </summary>
    private async Task<BalancePlan> PlanBankBalancesAsync(
        ImportResult<BankBalanceRow> parsed,
        DateOnly cutover,
        CancellationToken ct)
    {
        var positions = new List<PlannedPosition>();
        var errors = new List<BalanceRowOutcome>();
        AddParseErrorOutcomes(parsed.Errors, errors);

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
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalBankId, rawJson,
                    "name", $"No bank account named '{row.Name}' found in this org"));
                continue;
            }

            if (matches.Count > 1)
            {
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalBankId, rawJson,
                    "name", $"ambiguous_bank_name: {matches.Count} active bank accounts named '{row.Name}' — cannot route opening balance"));
                continue;
            }

            var bank = matches[0];
            var accountCode = bank.Purpose is DirectoryBankPurpose.Trust or DirectoryBankPurpose.Deposit
                ? AccountCodes.TrustBank(bank.Id)
                : AccountCodes.PmOperatingBank(bank.Id);

            var sourceRef = $"opening:{cutover:yyyy-MM-dd}:bank={bank.Id}";

            // bank is debit-normal; a figure of exactly $0.00 is still planned — PostLineAsync skips it.
            positions.Add(new PlannedPosition(
                rowNumber, row.ExternalBankId, rawJson, sourceRef,
                accountCode, DebitNormal: true, row.BookBalance, EntryBasis.Both,
                OwnerId: null, TenantId: null, BankAccountId: bank.Id));
        }

        return new BalancePlan(positions, errors);
    }

    /// <summary>
    /// owner_balances: resolves ownerId and the operating trust bank (Trust-purpose) and plans a
    /// cash position (Basis=Both, credit-normal) plus an accrual-delta position (Basis=Accrual,
    /// accrual − cash) for every resolved row — both are planned even when a figure is exactly
    /// $0.00; PostLineAsync skips a zero figure when the plan is posted.
    /// SourceRefs: "opening:{cutover}:owner-equity={ownerId}" + "…:owner-equity-accrual={ownerId}".
    /// </summary>
    private async Task<BalancePlan> PlanOwnerBalancesAsync(
        ImportResult<OwnerBalanceRow> parsed,
        DateOnly cutover,
        CancellationToken ct)
    {
        var positions = new List<PlannedPosition>();
        var errors = new List<BalanceRowOutcome>();
        AddParseErrorOutcomes(parsed.Errors, errors);

        var ownerMap = await resolver.BuildMapAsync(EntityKind.Owners, ct);
        var operatingTrustId = await ResolveOperatingTrustAsync(ct);

        if (operatingTrustId is null)
        {
            foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
            {
                var rawJson = SerializeRaw(new { row.ExternalOwnerId, row.Name, row.CashBalance, row.AccrualBalance });
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalOwnerId, rawJson,
                    "bank", "No operating trust bank account (Trust purpose) found in this org"));
            }
            return new BalancePlan(positions, errors);
        }

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalOwnerId, row.Name, row.CashBalance, row.AccrualBalance });

            if (!ownerMap.TryGetValue(row.ExternalOwnerId, out var ownerId))
            {
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalOwnerId, rawJson,
                    "external_owner_id",
                    $"'{row.ExternalOwnerId}' not found in imported owners"));
                continue;
            }

            // Two independent positions per row (Fix 1): the cash position (Basis=Both) and the
            // accrual-delta position (Basis=Accrual). Each is posted independently and skipped only
            // when ITS figure is $0.00, so cash=0/accrual=200 still posts the $200 accrual delta.
            // owner_equity is credit-normal.
            var cashSourceRef = $"opening:{cutover:yyyy-MM-dd}:owner-equity={ownerId}";
            positions.Add(new PlannedPosition(
                rowNumber, row.ExternalOwnerId, rawJson, cashSourceRef,
                AccountCodes.OwnerEquity, DebitNormal: false, row.CashBalance, EntryBasis.Both,
                OwnerId: ownerId, TenantId: null, BankAccountId: operatingTrustId));

            // Accrual-delta position: the (accrual − cash) delta tagged Accrual, distinct source_ref.
            // A delta of 0 (accrual == cash) posts as a no-op.
            var delta = row.AccrualBalance - row.CashBalance;
            var accrualSourceRef = $"opening:{cutover:yyyy-MM-dd}:owner-equity-accrual={ownerId}";
            positions.Add(new PlannedPosition(
                rowNumber, row.ExternalOwnerId, rawJson, accrualSourceRef,
                AccountCodes.OwnerEquity, DebitNormal: false, delta, EntryBasis.Accrual,
                OwnerId: ownerId, TenantId: null, BankAccountId: operatingTrustId));
        }

        return new BalancePlan(positions, errors);
    }

    /// <summary>
    /// deposit_liabilities: resolves tenantId and ownerId and plans a position against
    /// SecurityDepositsHeld (credit-normal, Basis=Both) against the deposit trust bank.
    /// SourceRef: "opening:{cutover}:deposit={tenantId}".
    /// </summary>
    private async Task<BalancePlan> PlanDepositLiabilitiesAsync(
        ImportResult<DepositLiabilityRow> parsed,
        DateOnly cutover,
        CancellationToken ct)
    {
        var positions = new List<PlannedPosition>();
        var errors = new List<BalanceRowOutcome>();
        AddParseErrorOutcomes(parsed.Errors, errors);

        var ownerMap = await resolver.BuildMapAsync(EntityKind.Owners, ct);
        var tenantMap = await BuildTenantMapAsync(ct);
        var depositTrustId = await ResolveDepositTrustAsync(ct);

        if (depositTrustId is null)
        {
            foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
            {
                var rawJson = SerializeRaw(new { row.ExternalTenantId, row.ExternalOwnerId, row.HeldAmount });
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalTenantId, rawJson,
                    "bank", "No deposit trust bank account (Deposit purpose) found in this org"));
            }
            return new BalancePlan(positions, errors);
        }

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalTenantId, row.ExternalOwnerId, row.HeldAmount });

            if (!tenantMap.TryGetValue(row.ExternalTenantId, out var tenantId))
            {
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalTenantId, rawJson,
                    "external_tenant_id",
                    $"'{row.ExternalTenantId}' not found in imported tenants"));
                continue;
            }

            if (!ownerMap.TryGetValue(row.ExternalOwnerId, out var ownerId))
            {
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalTenantId, rawJson,
                    "external_owner_id",
                    $"'{row.ExternalOwnerId}' not found in imported owners"));
                continue;
            }

            // SecurityDepositsHeld is credit-normal; a held amount of exactly $0.00 is still planned —
            // PostLineAsync skips it.
            var sourceRef = $"opening:{cutover:yyyy-MM-dd}:deposit={tenantId}";
            positions.Add(new PlannedPosition(
                rowNumber, row.ExternalTenantId, rawJson, sourceRef,
                AccountCodes.SecurityDepositsHeld, DebitNormal: false, row.HeldAmount, EntryBasis.Both,
                OwnerId: ownerId, TenantId: tenantId, BankAccountId: depositTrustId));
        }

        return new BalancePlan(positions, errors);
    }

    /// <summary>
    /// tenant_receivables: resolves tenantId and ownerId and plans a position against
    /// TenantReceivable (debit-normal, Basis=Accrual). No bank dimension.
    /// SourceRef: "opening:{cutover}:receivable={tenantId}".
    /// </summary>
    private async Task<BalancePlan> PlanTenantReceivablesAsync(
        ImportResult<TenantReceivableRow> parsed,
        DateOnly cutover,
        CancellationToken ct)
    {
        var positions = new List<PlannedPosition>();
        var errors = new List<BalanceRowOutcome>();
        AddParseErrorOutcomes(parsed.Errors, errors);

        var ownerMap = await resolver.BuildMapAsync(EntityKind.Owners, ct);
        var tenantMap = await BuildTenantMapAsync(ct);

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalTenantId, row.ExternalOwnerId, row.Balance });

            if (!tenantMap.TryGetValue(row.ExternalTenantId, out var tenantId))
            {
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalTenantId, rawJson,
                    "external_tenant_id",
                    $"'{row.ExternalTenantId}' not found in imported tenants"));
                continue;
            }

            if (!ownerMap.TryGetValue(row.ExternalOwnerId, out var ownerId))
            {
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalTenantId, rawJson,
                    "external_owner_id",
                    $"'{row.ExternalOwnerId}' not found in imported owners"));
                continue;
            }

            // TenantReceivable is debit-normal; a balance of exactly $0.00 is still planned —
            // PostLineAsync skips it. No bank dimension.
            var sourceRef = $"opening:{cutover:yyyy-MM-dd}:receivable={tenantId}";
            positions.Add(new PlannedPosition(
                rowNumber, row.ExternalTenantId, rawJson, sourceRef,
                AccountCodes.TenantReceivable, DebitNormal: true, row.Balance, EntryBasis.Accrual,
                OwnerId: ownerId, TenantId: tenantId, BankAccountId: null));
        }

        return new BalancePlan(positions, errors);
    }

    /// <summary>
    /// held_pm_fees (WP-7 Task 10 / ADR-020 §5): resolves the bank via the same case-insensitive
    /// name-match + ambiguity rejection as bank_balances, then gates on purpose — a Trust- or
    /// Deposit-purpose bank is trust_bank-class and accepted (§3.1/F1); an Operating-purpose match
    /// is a row error, never silently routed. Plans a credit-normal position against PmIncome (a
    /// negative HeldAmount silently flips to a pm_income debit — D9, deliberately legal, no guard).
    /// SourceRef: "opening:{cutover}:held-fees={bankId}".
    /// </summary>
    private async Task<BalancePlan> PlanHeldPmFeesAsync(
        ImportResult<HeldPmFeeRow> parsed,
        DateOnly cutover,
        CancellationToken ct)
    {
        var positions = new List<PlannedPosition>();
        var errors = new List<BalanceRowOutcome>();
        AddParseErrorOutcomes(parsed.Errors, errors);

        var bankAccounts = await db.Set<BankAccount>()
            .AsNoTracking()
            .Where(b => b.IsActive)
            .ToListAsync(ct);

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalBankId, row.Name, row.HeldAmount });

            // Case-insensitive name match; two same-named active banks would misroute held fees, so
            // an ambiguous match is a row error, not a guess (same rule as bank_balances).
            var matches = bankAccounts
                .Where(b => string.Equals(b.Name, row.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalBankId, rawJson,
                    "name", $"No bank account named '{row.Name}' found in this org"));
                continue;
            }

            if (matches.Count > 1)
            {
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalBankId, rawJson,
                    "name", $"ambiguous_bank_name: {matches.Count} active bank accounts named '{row.Name}' — cannot route held fees"));
                continue;
            }

            var bank = matches[0];

            // §3.1/F1: Trust- AND Deposit-purpose banks are trust_bank-class and accepted; only an
            // Operating-purpose (pm_operating_bank) match is rejected.
            if (bank.Purpose == DirectoryBankPurpose.Operating)
            {
                errors.Add(BalanceRowOutcome.Error(rowNumber, row.ExternalBankId, rawJson,
                    "name", $"'{row.Name}' is an operating account — held fees can only be imported into a trust bank account"));
                continue;
            }

            var sourceRef = $"opening:{cutover:yyyy-MM-dd}:held-fees={bank.Id}";

            // pm_income is credit-normal; a held amount of exactly $0.00 is still planned —
            // PostLineAsync skips it, uniform with every other balance kind.
            positions.Add(new PlannedPosition(
                rowNumber, row.ExternalBankId, rawJson, sourceRef,
                AccountCodes.PmIncome, DebitNormal: false, row.HeldAmount, EntryBasis.Both,
                OwnerId: null, TenantId: null, BankAccountId: bank.Id));
        }

        return new BalancePlan(positions, errors);
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

    // -------------------------------------------------------------------------
    // Planner shapes (WP-7 Task 4) — private nested like BalanceRowOutcome above, since Task 5's
    // supersede engine lives in this same file and needs no wider visibility.
    // -------------------------------------------------------------------------

    /// <summary>One resolvable opening position derived from a CSV row (figure may be 0).</summary>
    private sealed record PlannedPosition(
        int RowNumber, string ExternalId, string RawJson, string SourceRef,
        string AccountCode, bool DebitNormal, decimal Figure, EntryBasis Basis,
        Guid? OwnerId, Guid? TenantId, Guid? BankAccountId);

    /// <summary>
    /// The result of <see cref="PlanAsync"/>: every resolvable row's position(s) plus every
    /// unresolvable row's error. A given <see cref="PlannedPosition.RowNumber"/> appears in
    /// exactly one of the two lists, never both.
    /// </summary>
    private sealed record BalancePlan(
        List<PlannedPosition> Positions, List<BalanceRowOutcome> ResolutionErrors);
}

/// <summary>
/// A typed pre-flight conflict raised by <see cref="BalanceImportService.SupersedeAsync"/> before any
/// write (the §2 guards). The onboarding endpoint (WP-7 Task 6) maps <see cref="Code"/> to a 409
/// ProblemDetails; the message is display-safe (dates and instructions only — no ids, account codes,
/// or table names), so technical detail belongs in the log, not here.
/// </summary>
public sealed class SupersedeConflictException(string code, string detail) : Exception(detail)
{
    /// <summary><c>already_signed_off</c> | <c>nothing_to_supersede</c> | <c>cutover_date_mismatch</c>.</summary>
    public string Code { get; } = code;
}
