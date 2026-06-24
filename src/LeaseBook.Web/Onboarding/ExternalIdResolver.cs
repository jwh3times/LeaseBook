using System.Text.Json;
using LeaseBook.Migrator.Model;
using LeaseBook.Web.Onboarding.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Onboarding;

/// <summary>
/// Builds a reverse lookup (external id → LeaseBook id) from the org's prior <c>posted</c>
/// <see cref="ImportRow"/>s for a given <see cref="EntityKind"/>. Used during entity import so
/// child rows can resolve parent FKs (e.g. a property row's <c>external_owner_id</c> →
/// the owner's LeaseBook <see cref="Guid"/>). 3.2 balance import reuses the same helper.
///
/// Each <c>import_rows.mapped_json</c> for entity rows is a JSON object with at least
/// <c>external_id</c> and <c>leaseBookId</c> keys written by <see cref="EntityImportService"/>.
/// </summary>
public sealed class ExternalIdResolver(DbContext db)
{
    /// <summary>
    /// Returns a dictionary mapping external id → LeaseBook id for all <c>posted</c> rows of
    /// the given kind. Reads within the ambient RLS transaction (no new connection).
    /// </summary>
    public async Task<Dictionary<string, Guid>> BuildMapAsync(EntityKind kind, CancellationToken ct)
    {
        var kindStr = kind.ToString();

        // RLS scopes this to the current org automatically. A batch with ≥1 bad row becomes
        // "posted_with_errors", but its successfully-created rows still carry RowStatus == "posted"
        // and a real leaseBookId — so include both batch statuses and filter to good rows at the
        // row level. Filtering only at the batch level would drop every good row of a mixed batch.
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
                    doc.RootElement.TryGetProperty("leaseBookId", out var lbEl) &&
                    extEl.ValueKind == JsonValueKind.String &&
                    lbEl.TryGetGuid(out var lbId))
                {
                    result[extEl.GetString()!] = lbId;
                }
            }
            catch (JsonException)
            {
                // Corrupt mapped_json — skip silently. Should not happen for rows we wrote.
            }
        }

        return result;
    }
}
