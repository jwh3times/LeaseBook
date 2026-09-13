using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Statements;

/// <param name="Anchors">
/// Per owner, the statement already issued for the <b>preceding</b> period (ADR-045). When present, the
/// statement decomposes its beginning balance into the figure that statement closed at plus the
/// prior-period adjustments posted since. Absent owners get no carry-forward.
/// </param>
public sealed record GetOwnerStatementData(
    IReadOnlyList<Guid> OwnerIds, Guid? PropertyId, int Year, int Month, string Basis,
    IReadOnlyDictionary<Guid, StatementAnchor>? Anchors = null)
    : IQuery<OwnerStatementDataResponse>;

public sealed class GetOwnerStatementDataValidator : AbstractValidator<GetOwnerStatementData>
{
    public GetOwnerStatementDataValidator()
    {
        RuleFor(q => q.OwnerIds).NotEmpty();
        RuleFor(q => q.Year).InclusiveBetween(2000, 2100);
        RuleFor(q => q.Month).InclusiveBetween(1, 12);
        RuleFor(q => q.Basis).Must(b => b is "cash" or "accrual").WithMessage("Basis must be 'cash' or 'accrual'.");
        RuleFor(q => q.Anchors)
            .Must((q, anchors) => anchors is null || anchors.Keys.All(q.OwnerIds.Contains))
            .WithMessage("Every statement anchor must belong to a requested owner.")
            .Must(anchors => anchors is null || anchors.Values.All(a => a.AsOf.Kind == DateTimeKind.Utc))
            .WithMessage("Statement anchor instants must be UTC.");
    }
}

/// <summary>
/// What an issued statement for the preceding period recorded (ADR-045): the ending balance it
/// presented, and the instant its figures were read. Everything posted after that instant and dated
/// into or before that period is what the owner has not yet been shown.
/// </summary>
public sealed record StatementAnchor(decimal IssuedEnding, DateTime AsOf);

public sealed record OwnerStatementDataResponse(IReadOnlyDictionary<Guid, OwnerStatement> ByOwner);

/// <param name="AsOf">
/// The instant this statement's figures were read. An issued statement stores it so its successor can
/// tell which postings it could not have included.
/// </param>
/// <param name="Adjustments">Null unless an anchor was supplied for this owner.</param>
public sealed record OwnerStatement(Guid OwnerId, Guid? PropertyId, string Basis, int Year, int Month,
    decimal Beginning, IReadOnlyList<StatementSection> Sections, decimal Ending, StatementTieOut TieOut,
    DateTime AsOf, PriorPeriodAdjustments? Adjustments);
public sealed record StatementSection(StatementSectionKey Key, string Title, IReadOnlyList<StatementLine> Lines, decimal Subtotal);
public sealed record StatementLine(Guid EntryId, DateOnly Date, string EventType, string? EventSubtype,
    string Description, Guid? PropertyId, decimal Amount);
public sealed record StatementTieOut(bool Balanced, decimal Variance, bool PmIncomeExcluded, bool DepositsRecognizedOnApplication);

/// <summary>
/// The carry-forward from an issued statement to this one (ADR-045):
/// <c>IssuedEnding + Total = Beginning</c>, always, because <see cref="Total"/> is defined as that
/// difference. The issued figure is authoritative; <see cref="Lines"/> explain it.
/// </summary>
/// <param name="Lines">
/// Entries dated into the prior period or earlier whose <c>posted_at</c> is after the anchor's
/// <c>AsOf</c>, plus every opening position dated into this period — those belong to Beginning but can
/// never be part of the prior period's issued ending, whenever they were posted.
/// </param>
/// <param name="Unitemized">
/// <c>Total − Σ Lines</c>. Zero unless a posting transaction was still open while the anchoring
/// statement was read: <c>posted_at</c> is stamped when an entry is posted but the rows become visible
/// only at commit, and a bulk run commits many entries at its end. Never silently absorbed.
/// </param>
public sealed record PriorPeriodAdjustments(
    decimal IssuedEnding, DateTime IssuedAsOf, IReadOnlyList<StatementAdjustmentLine> Lines,
    decimal Unitemized, decimal Total);

public sealed record StatementAdjustmentLine(Guid EntryId, DateOnly Date, DateTime PostedAt, string EventType,
    string? EventSubtype, string Description, Guid? PropertyId, decimal Amount);

internal sealed class GetOwnerStatementDataHandler(DbContext db)
    : IQueryHandler<GetOwnerStatementData, OwnerStatementDataResponse>
{
    private sealed record Row(Guid OwnerId, Guid EntryId, DateOnly Date, string EventType, string? EventSubtype,
        string Description, Guid? PropertyId, decimal Amount);
    private sealed record AdjustmentRow(Guid OwnerId, Guid EntryId, DateOnly Date, DateTime PostedAt, string EventType,
        string? EventSubtype, string Description, Guid? PropertyId, decimal Amount);
    private sealed record Begin(Guid OwnerId, decimal Amount);
    private sealed record EndBalance(Guid OwnerId, decimal Amount);

    public async Task<OwnerStatementDataResponse> Handle(GetOwnerStatementData q, CancellationToken ct)
    {
        // Taken before any read, on the same clock that stamps journal_entries.posted_at. An entry whose
        // transaction straddles this instant may or may not be visible to the reads below; its
        // successor's carry-forward accounts for either case (the Unitemized remainder), so no posting
        // is lost — only, in that case, left unattributed to a line.
        var asOf = DateTime.UtcNow;

        var owners = q.OwnerIds.ToArray();
        var start = new DateOnly(q.Year, q.Month, 1);
        var end = start.AddMonths(1); // exclusive

        // In-period owner-equity movement, one row per (entry, property), with event metadata. Event
        // type is resolved through the reversal link (COALESCE(orig.event_type, e.event_type)): a
        // void lands in the section of whatever it reverses, not a separate "EntryVoided" bucket.
        // Opening-typed entries (OpeningBalance/BalanceForward) are excluded here because they fold
        // into Beginning instead of appearing as in-period movement (see the `begins` query below).
        var rows = await db.Database.SqlQuery<Row>(
            $"""
            SELECT jl.owner_id, e.id AS entry_id, e.entry_date AS date,
                   COALESCE(orig.event_type, e.event_type) AS event_type, e.event_subtype,
                   e.description, jl.property_id,
                   SUM(COALESCE(jl.credit,0) - COALESCE(jl.debit,0)) AS amount
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            LEFT JOIN journal_entries orig ON orig.id = e.reverses_entry_id
            WHERE jl.owner_id = ANY({owners}) AND jl.account_class = 'owner_equity'
              AND jl.basis IN ({q.Basis}, 'both')
              AND ({q.PropertyId}::uuid IS NULL OR jl.property_id = {q.PropertyId})
              AND e.entry_date >= {start} AND e.entry_date < {end}
              AND COALESCE(orig.event_type, e.event_type) NOT IN ('OpeningBalance', 'BalanceForward')
            GROUP BY jl.owner_id, e.id, e.entry_date, COALESCE(orig.event_type, e.event_type),
                     e.event_subtype, e.description, jl.property_id
            ORDER BY e.entry_date, e.posted_at, e.id
            """).ToListAsync(ct);

        // Beginning balance = cumulative owner-equity before the period start. Also picks up an
        // in-period opening-typed entry (OpeningBalance/BalanceForward, resolved through the same
        // reversal-link COALESCE): the M7 per-position import posts its opening position dated at
        // cutover, which can fall inside the statement period, and it belongs in Beginning rather
        // than as an in-period movement line.
        var begins = await db.Database.SqlQuery<Begin>(
            $"""
            SELECT jl.owner_id, COALESCE(SUM(COALESCE(jl.credit,0) - COALESCE(jl.debit,0)), 0) AS amount
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            LEFT JOIN journal_entries orig ON orig.id = e.reverses_entry_id
            WHERE jl.owner_id = ANY({owners}) AND jl.account_class = 'owner_equity'
              AND jl.basis IN ({q.Basis}, 'both')
              AND ({q.PropertyId}::uuid IS NULL OR jl.property_id = {q.PropertyId})
              AND (e.entry_date < {start}
                   OR (e.entry_date < {end}
                       AND COALESCE(orig.event_type, e.event_type) IN ('OpeningBalance', 'BalanceForward')))
            GROUP BY jl.owner_id
            """).ToListAsync(ct);
        var beginning = begins.ToDictionary(b => b.OwnerId, b => b.Amount);

        // Independent period-end cumulative balance re-queried directly from the journal.
        // Same owner/basis/property filters as the queries above, boundary e.entry_date < {end}, and
        // deliberately NO reversal join or event-type predicate — this is the independent cumulative
        // check. This makes the tie-out structural: a categorization or sign slip in the C# section
        // pipeline produces a non-zero variance rather than silently computing x - x = 0.
        var endBalancesFromJournal = await db.Database.SqlQuery<EndBalance>(
            $"""
            SELECT jl.owner_id, COALESCE(SUM(COALESCE(jl.credit,0) - COALESCE(jl.debit,0)), 0) AS amount
            FROM journal_lines jl JOIN journal_entries e ON e.id = jl.entry_id
            WHERE jl.owner_id = ANY({owners}) AND jl.account_class = 'owner_equity'
              AND jl.basis IN ({q.Basis}, 'both')
              AND ({q.PropertyId}::uuid IS NULL OR jl.property_id = {q.PropertyId})
              AND e.entry_date < {end}
            GROUP BY jl.owner_id
            """).ToListAsync(ct);
        var endBalanceMap = endBalancesFromJournal.ToDictionary(b => b.OwnerId, b => b.Amount);

        var adjustmentRows = await ReadAdjustmentRowsAsync(q, start, end, ct);

        var byOwner = new Dictionary<Guid, OwnerStatement>();
        foreach (var ownerId in owners)
        {
            var begin = beginning.GetValueOrDefault(ownerId, 0m);
            var mine = rows.Where(r => r.OwnerId == ownerId).ToList();

            var sections = mine
                .GroupBy(r => StatementSectionMap.Section(r.EventType)) // throws on an unmapped event
                .OrderBy(g => (int)g.Key)
                .Select(g => new StatementSection(g.Key, StatementSectionMap.Title(g.Key),
                    g.Select(r => new StatementLine(r.EntryId, r.Date, r.EventType, r.EventSubtype,
                        r.Description, r.PropertyId, r.Amount)).ToList(),
                    g.Sum(r => r.Amount)))
                .ToList();

            // ending: the statement's own arithmetic (begin + categorized section subtotals).
            var ending = begin + sections.Sum(s => s.Subtotal);

            // Tie-out: compare statement arithmetic against an independent journal re-query.
            // A categorization or sign error in the C# pipeline produces a non-zero variance.
            var endBalanceFromJournal = endBalanceMap.GetValueOrDefault(ownerId, 0m);
            var variance = ending - endBalanceFromJournal;
            var applied = sections.Any(s => s.Key == StatementSectionKey.AppliedDepositsCredits);

            byOwner[ownerId] = new OwnerStatement(ownerId, q.PropertyId, q.Basis, q.Year, q.Month,
                begin, sections, ending,
                new StatementTieOut(Balanced: variance == 0m, variance,
                    PmIncomeExcluded: true /* structural: query is owner_equity + owner_id-scoped */,
                    DepositsRecognizedOnApplication: applied),
                asOf,
                CarryForward(ownerId, begin, q.Anchors, adjustmentRows));
        }

        return new OwnerStatementDataResponse(byOwner);
    }

    /// <summary>
    /// Owner-equity movement in Beginning that the anchored statement could not have shown, one row per
    /// (entry, property) like the in-period read. Two disjoint parts of the Beginning predicate:
    /// <list type="bullet">
    /// <item>entries dated before this period, posted after each owner's own anchor instant;</item>
    /// <item>opening positions dated into this period, regardless of when they were posted — the prior
    /// period's ending excludes them by date, so a pre-anchor posting of one is still a difference the
    /// owner has not been shown, not an unexplained one.</item>
    /// </list>
    /// </summary>
    private async Task<List<AdjustmentRow>> ReadAdjustmentRowsAsync(
        GetOwnerStatementData q, DateOnly start, DateOnly end, CancellationToken ct)
    {
        if (q.Anchors is not { Count: > 0 } anchors)
        {
            return [];
        }

        var anchorOwners = anchors.Keys.ToArray();
        var anchorInstants = anchorOwners.Select(o => anchors[o].AsOf).ToArray();

        return await db.Database.SqlQuery<AdjustmentRow>(
            $"""
            SELECT jl.owner_id, e.id AS entry_id, e.entry_date AS date, e.posted_at,
                   COALESCE(orig.event_type, e.event_type) AS event_type, e.event_subtype,
                   e.description, jl.property_id,
                   SUM(COALESCE(jl.credit,0) - COALESCE(jl.debit,0)) AS amount
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            LEFT JOIN journal_entries orig ON orig.id = e.reverses_entry_id
            JOIN unnest({anchorOwners}, {anchorInstants}) AS a(owner_id, as_of) ON a.owner_id = jl.owner_id
            WHERE jl.account_class = 'owner_equity'
              AND jl.basis IN ({q.Basis}, 'both')
              AND ({q.PropertyId}::uuid IS NULL OR jl.property_id = {q.PropertyId})
              AND ((e.entry_date < {start} AND e.posted_at > a.as_of)
                   OR (e.entry_date >= {start} AND e.entry_date < {end}
                       AND COALESCE(orig.event_type, e.event_type) IN ('OpeningBalance', 'BalanceForward')))
            GROUP BY jl.owner_id, e.id, e.entry_date, e.posted_at, COALESCE(orig.event_type, e.event_type),
                     e.event_subtype, e.description, jl.property_id
            ORDER BY e.entry_date, e.posted_at, e.id
            """).ToListAsync(ct);
    }

    private static PriorPeriodAdjustments? CarryForward(
        Guid ownerId, decimal beginning, IReadOnlyDictionary<Guid, StatementAnchor>? anchors,
        List<AdjustmentRow> adjustmentRows)
    {
        if (anchors is null || !anchors.TryGetValue(ownerId, out var anchor))
        {
            return null;
        }

        var lines = adjustmentRows
            .Where(r => r.OwnerId == ownerId)
            .Select(r => new StatementAdjustmentLine(r.EntryId, r.Date, r.PostedAt, r.EventType, r.EventSubtype,
                r.Description, r.PropertyId, r.Amount))
            .ToList();

        // The issued figure is authoritative, so the total is the exact difference and can never
        // disagree with Beginning. The itemized lines explain it; any remainder is surfaced, not hidden.
        var total = beginning - anchor.IssuedEnding;
        return new PriorPeriodAdjustments(anchor.IssuedEnding, anchor.AsOf, lines, total - lines.Sum(l => l.Amount), total);
    }
}
