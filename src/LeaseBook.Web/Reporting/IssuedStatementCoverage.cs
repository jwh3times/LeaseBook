using System.Text.Json;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Reporting.Contracts;
using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// One already-issued owner statement that a posting will be carried forward into (ADR-045, #377):
/// the latest statement issued for that owner, basis and scope on or after the posting's month.
/// </summary>
/// <param name="PropertyId">The statement's property scope, or null for a whole-owner statement.</param>
public sealed record IssuedStatementCoverageRow(
    Guid OwnerId,
    string OwnerName,
    string Basis,
    Guid? PropertyId,
    string? PropertyAddress,
    int IssuedYear,
    int IssuedMonth);

/// <summary>SPA shape for <c>GET /api/statements/issued-coverage</c>.</summary>
public sealed record IssuedStatementCoverageResponse(IReadOnlyList<IssuedStatementCoverageRow> Rows);

/// <summary>
/// Which issued owner statements a set of postings will carry forward into — the read behind the
/// non-blocking notice shown to whoever posted them (#377).
/// <para>
/// Deliberately a separate read after posting, never part of it: nothing about issued statements
/// reaches posting code, a posting response, a run's confirm contract or its capability freeze, so the
/// rule that no issued-statement fact may become a posting-template argument holds by construction.
/// </para>
/// <para>
/// Host-owned composition (ADR-016): it reads Accounting's owner-equity lines through
/// <see cref="ISender"/>, the Reporting module's artifact history and the Operations module's run items
/// on the request's own context, the same way <see cref="LocalStatementDelivery"/> and the operations
/// endpoints already do.
/// </para>
/// </summary>
public static class IssuedStatementCoverage
{
    /// <summary>The most entry ids a caller may pass directly; a run is looked up by its id instead.</summary>
    public const int MaxEntryIds = 200;

    /// <summary>What matching needs of an issued statement artifact.</summary>
    public sealed record IssuedStatement(
        Guid OwnerId, int PeriodYear, int PeriodMonth, string Basis, Guid? PropertyId, DateTime AsOf);

    /// <summary>A matched (owner, basis, scope) and the latest issued period it lands after.</summary>
    public sealed record CoverageMatch(Guid OwnerId, string Basis, Guid? PropertyId, int IssuedYear, int IssuedMonth);

    /// <summary>
    /// The exact rule ADR-045's carry-forward applies, per owner-equity line. An issued statement covers a
    /// line when all of these hold:
    /// <list type="bullet">
    /// <item>it is the same owner;</item>
    /// <item>its basis is one the line touches (a <c>both</c> line touches cash and accrual statements);</item>
    /// <item>it is whole-owner, or scoped to the line's property;</item>
    /// <item>its period is the entry's month or later — earlier statements never show the entry;</item>
    /// <item>its figures were read <b>before</b> the entry was posted — a statement issued afterwards
    /// already includes the entry, so nothing about it goes stale.</item>
    /// </list>
    /// Of the statements that cover a line, only the latest per (owner, basis, scope) is reported: that is
    /// the document now out of date, and the one whose immediate successor will itemize the entry.
    /// <para>
    /// Netting follows the statement, not the stored line: a statement reads <c>its basis + both</c> and,
    /// when whole-owner, every property, so an entry whose lines cancel under that reading moves nothing
    /// the statement shows and is not reported against it — exactly as the carry-forward hides a zero
    /// section.
    /// </para>
    /// <para>
    /// Not modelled: an opening position dated <i>into</i> a period is itemized by that period's
    /// carry-forward regardless of posting time (ADR-045 §3). No screen that shows this notice posts
    /// opening positions — they are dated at cutover, before any statement is issued.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CoverageMatch> Match(
        IReadOnlyList<OwnerEquityLine> lines, IReadOnlyList<IssuedStatement> issued)
    {
        var latest = new Dictionary<(Guid OwnerId, string Basis, Guid? PropertyId), int>();

        foreach (var statement in issued)
        {
            var statementPeriod = PeriodIndex(statement.PeriodYear, statement.PeriodMonth);

            // Net, per entry, exactly the lines this statement would read and could not yet have shown.
            var movesStatement = lines
                .Where(line => line.OwnerId == statement.OwnerId
                    && (line.Basis == "both" || line.Basis == statement.Basis)
                    && (statement.PropertyId is not { } scope || scope == line.PropertyId)
                    && statementPeriod >= PeriodIndex(line.EntryDate.Year, line.EntryDate.Month)
                    && statement.AsOf < line.PostedAt)
                .GroupBy(line => line.EntryId)
                .Any(entry => entry.Sum(line => line.Amount) != 0m);

            if (!movesStatement)
            {
                continue;
            }

            var key = (statement.OwnerId, statement.Basis, statement.PropertyId);
            if (!latest.TryGetValue(key, out var current) || statementPeriod > current)
            {
                latest[key] = statementPeriod;
            }
        }

        return latest
            .Select(kv => new CoverageMatch(
                kv.Key.OwnerId, kv.Key.Basis, kv.Key.PropertyId, kv.Value / 12, (kv.Value % 12) + 1))
            .OrderBy(m => m.OwnerId)
            .ThenBy(m => m.Basis, StringComparer.Ordinal)
            .ThenBy(m => m.PropertyId)
            .ToList();
    }

    /// <summary>
    /// The journal entries a confirmed run posted: each posted item's resulting entry, plus the separate
    /// management-fee entry a disbursement records in its snapshot (<c>feeEntryId</c>) — that entry moves
    /// owner equity too. Scoped by the request's organization like every other run read.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> RunEntryIdsAsync(AppDbContext db, Guid runId, CancellationToken ct)
    {
        var items = await db.Set<BulkRunItem>()
            .Where(i => i.RunId == runId && i.Status == RunItemStatus.Posted && i.ResultingJournalEntryId != null)
            .Select(i => new { EntryId = i.ResultingJournalEntryId!.Value, i.SnapshotJson })
            .ToListAsync(ct);

        var ids = new List<Guid>(items.Count * 2);
        foreach (var item in items)
        {
            ids.Add(item.EntryId);

            using var snapshot = JsonDocument.Parse(item.SnapshotJson);
            if (snapshot.RootElement.ValueKind == JsonValueKind.Object
                && snapshot.RootElement.TryGetProperty("feeEntryId", out var fee)
                && fee.ValueKind == JsonValueKind.String
                && Guid.TryParse(fee.GetString(), out var feeEntryId))
            {
                ids.Add(feeEntryId);
            }
        }

        return ids.Distinct().ToList();
    }

    /// <summary>Resolves coverage for <paramref name="entryIds"/> into display rows.</summary>
    public static async Task<IssuedStatementCoverageResponse> ReadAsync(
        IReadOnlyList<Guid> entryIds, ISender sender, AppDbContext db, IStatementNames names, CancellationToken ct)
    {
        if (entryIds.Count == 0)
        {
            return new IssuedStatementCoverageResponse([]);
        }

        // Chunked to the query's own bound: a large run (a rent run over many leases, a disbursement with
        // a fee entry per owner) must still be checked, not refused.
        var lines = new List<OwnerEquityLine>();
        foreach (var chunk in entryIds.Chunk(GetOwnerEquityLinesValidator.MaxEntryIds))
        {
            lines.AddRange(await sender.Query(new GetOwnerEquityLines(chunk), ct));
        }

        if (lines.Count == 0)
        {
            return new IssuedStatementCoverageResponse([]);
        }

        var owners = lines.Select(l => l.OwnerId).Distinct().ToArray();

        // Only artifacts that recorded their figures can be carried forward from (ADR-045 §1); the rest
        // predate the decision and anchor nothing, so they cannot go stale in the sense this reports.
        var issued = await db.Set<StatementArtifact>()
            .Where(a => owners.Contains(a.OwnerId) && a.Basis != null && a.EndingBalance != null && a.AsOf != null)
            .Select(a => new { a.OwnerId, a.PeriodYear, a.PeriodMonth, a.Basis, a.PropertyId, a.AsOf })
            .ToListAsync(ct);

        var matches = Match(
            lines,
            issued.Select(a => new IssuedStatement(
                a.OwnerId, a.PeriodYear, a.PeriodMonth, a.Basis!, a.PropertyId,
                DateTime.SpecifyKind(a.AsOf!.Value, DateTimeKind.Utc))).ToList());

        if (matches.Count == 0)
        {
            return new IssuedStatementCoverageResponse([]);
        }

        var ownerNames = await names.GetOwnerNamesAsync(ct);
        var addresses = await names.GetPropertyAddressesAsync(ct);

        return new IssuedStatementCoverageResponse(matches
            .Select(m => new IssuedStatementCoverageRow(
                m.OwnerId,
                ownerNames.GetValueOrDefault(m.OwnerId, "Unknown owner"),
                m.Basis,
                m.PropertyId,
                m.PropertyId is { } p ? addresses.GetValueOrDefault(p) : null,
                m.IssuedYear,
                m.IssuedMonth))
            .OrderBy(r => r.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Basis, StringComparer.Ordinal)
            .ThenBy(r => r.PropertyAddress, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static int PeriodIndex(int year, int month) => (year * 12) + (month - 1);
}
