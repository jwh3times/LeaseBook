using System.Diagnostics;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Posting;

/// <summary>
/// The single write path to the journal (WP-04). Explicit over clever: every rejection is a typed
/// <see cref="AccountingDomainException"/>; nothing is silently fixed up. Runs inside the ambient org
/// transaction (the request middleware or <c>OrgScopedExecutor</c>), which provides atomicity and the
/// SaveChanges savepoint the idempotency backstop relies on.
/// </summary>
internal sealed class PostingService(
    DbContext db, ITenantContext tenant, IAccountingPeriods periods,
    IActorContext? actor = null, IReconciliationLock? reconciliationLock = null) : IPostingService
{
    private static readonly EntryBasis[] BalancedBases = [EntryBasis.Cash, EntryBasis.Accrual];

    public async Task<Guid> PostAsync(PostEntryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // (a) org context present — fail fast before any work (the DB would also fail closed).
        if (tenant.OrgId is null)
        {
            throw new InvalidOperationException(
                "PostingService requires an ambient org context (request middleware or OrgScopedExecutor).");
        }

        if (request.Lines.Count == 0)
        {
            throw new InvalidLineException("An entry must have at least one line.");
        }

        // (b) resolve every referenced account through the org-filtered context — a cross-org code is
        // simply invisible here, so it surfaces as unknown_account rather than an RLS-bypassing FK.
        var codes = request.Lines.Select(l => l.AccountCode).Distinct(StringComparer.Ordinal).ToArray();
        var accounts = await db.Set<Account>()
            .Where(a => codes.Contains(a.Code))
            .ToDictionaryAsync(a => a.Code, StringComparer.Ordinal, ct);

        var lines = new List<JournalLine>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            if (!accounts.TryGetValue(line.AccountCode, out var account))
            {
                throw new UnknownAccountException(line.AccountCode);
            }

            // (c) exactly one of debit/credit, strictly positive (Money already guarantees scale ≤ 2).
            var hasDebit = line.Debit is not null;
            var hasCredit = line.Credit is not null;
            if (hasDebit == hasCredit)
            {
                throw new InvalidLineException(
                    $"Line on '{line.AccountCode}' must have exactly one of debit/credit set.");
            }

            var amount = (line.Debit ?? line.Credit)!.Value;
            if (!amount.IsPositive)
            {
                throw new InvalidLineException(
                    $"Line on '{line.AccountCode}' must have a strictly positive amount, was {amount}.");
            }

            // (e) denormalize account_class from the resolved account — never trust the caller (M-E4).
            // (f) a pm_income line may not carry an owner dimension (the structural isolation).
            if (account.Class == AccountClass.PmIncome && line.OwnerId is not null)
            {
                throw new PmIncomeOwnerDimException(
                    "A pm_income line may not carry an owner_id — PM income is isolated from owner income.");
            }

            lines.Add(JournalLine.Create(
                account.Id, account.Class, line.Debit, line.Credit, line.Basis,
                line.PropertyId, line.UnitId, line.OwnerId, line.TenantId, line.BankAccountId, line.Memo));
        }

        // (d) per-basis balance: for cash and for accrual, debits == credits over {that basis, both}.
        foreach (var basis in BalancedBases)
        {
            var debits = 0m;
            var credits = 0m;
            foreach (var line in lines)
            {
                if (line.Basis != basis && line.Basis != EntryBasis.Both)
                {
                    continue;
                }

                debits += line.Debit?.Amount ?? 0m;
                credits += line.Credit?.Amount ?? 0m;
            }

            if (debits != credits)
            {
                throw new UnbalancedEntryException(
                    $"Entry does not balance in {basis} basis: debits {debits:0.00} != credits {credits:0.00}.");
            }
        }

        // (g) the period for entry_date must be open (lazy get-or-create, P32).
        var period = await periods.GetOpenPeriodAsync(request.EntryDate, ct);
        if (period.Status == PeriodStatus.Closed)
        {
            throw new PeriodClosedException(period.Year, period.Month);
        }

        // (g2) reconciliation lock (M4 / P63): a bank-account line into a finalized (account, month) is
        // rejected. Only lines whose account IS a bank (not attribution lines that merely carry the
        // bank dimension) move the bank book, so only those are gated.
        if (reconciliationLock is not null)
        {
            foreach (var bankId in lines
                .Where(l => l.BankAccountId is not null
                    && (l.AccountClass == AccountClass.TrustBank || l.AccountClass == AccountClass.PmOperatingBank))
                .Select(l => l.BankAccountId!.Value)
                .Distinct())
            {
                await reconciliationLock.EnsureOpenAsync(bankId, request.EntryDate, ct);
            }
        }

        // (h) idempotency: a present source_ref must be unique per org. Pre-check the common case; the
        // partial unique index is the race backstop, mapped in the catch below.
        if (request.SourceRef is not null)
        {
            var duplicate = await db.Set<JournalEntry>().AsNoTracking()
                .FirstOrDefaultAsync(e => e.SourceRef == request.SourceRef, ct);
            if (duplicate is not null)
            {
                throw new DuplicateSourceRefException(request.SourceRef, duplicate.Id);
            }
        }

        var entry = JournalEntry.Create(
            request.EntryDate, request.EventType, request.EventSubtype, request.Description,
            request.SourceRef, request.ReversesEntryId, createdBy: actor?.UserId, postedAt: DateTime.UtcNow);
        foreach (var line in lines)
        {
            entry.AddLine(line);
        }

        db.Set<JournalEntry>().Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The unique-index backstop fired (a racing post). EF rolled back to its SaveChanges
            // savepoint, so the transaction is intact and the winner's row is now visible; map the
            // conflict to the right typed error or re-throw if it was something else.
            db.ChangeTracker.Clear();
            await ThrowMappedUniqueViolationAsync(request, ct);
            throw;
        }

        EmitPostedEvent(entry);
        return entry.Id;
    }

    private async Task ThrowMappedUniqueViolationAsync(PostEntryRequest request, CancellationToken ct)
    {
        if (request.SourceRef is not null)
        {
            var existing = await db.Set<JournalEntry>().AsNoTracking()
                .FirstOrDefaultAsync(e => e.SourceRef == request.SourceRef, ct);
            if (existing is not null)
            {
                throw new DuplicateSourceRefException(request.SourceRef, existing.Id);
            }
        }

        if (request.ReversesEntryId is Guid reversed)
        {
            var existing = await db.Set<JournalEntry>().AsNoTracking()
                .FirstOrDefaultAsync(e => e.ReversesEntryId == reversed, ct);
            if (existing is not null)
            {
                throw new AlreadyReversedException($"Entry {reversed} has already been reversed.");
            }
        }
    }

    private static void EmitPostedEvent(JournalEntry entry) =>
        Activity.Current?.AddEvent(new ActivityEvent(
            "accounting.entry_posted",
            tags: new ActivityTagsCollection
            {
                { "event_type", entry.EventType },
                { "event_subtype", entry.EventSubtype },
                { "basis_line_count", entry.Lines.Count },
                { "entry_id", entry.Id },
            }));
}
