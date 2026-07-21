using System.Text.Json;
using LeaseBook.Migrator;
using LeaseBook.Migrator.Csv;
using LeaseBook.Migrator.Model;
using LeaseBook.Migrator.Profiles;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Observability;
using LeaseBook.Web.Onboarding.Persistence;
using Microsoft.EntityFrameworkCore;
// Alias avoids the UnitRow ambiguity (LeaseBook.Migrator.Model.UnitRow vs
// LeaseBook.Modules.Directory.Features.Units.UnitRow — both public).
using MigratorModel = LeaseBook.Migrator.Model;

namespace LeaseBook.Web.Onboarding;

/// <summary>
/// Per-outcome row counts for one import batch (S1: "nothing fails silently" — an already-posted
/// row is no longer folded into one undifferentiated success count). Ordinary entity and balance
/// imports report <see cref="Unchanged"/> = <see cref="Superseded"/> = 0; those two are reserved
/// for reconciliation-batch semantics introduced later in WP-7.
/// </summary>
public sealed record ImportOutcomeCounts(
    int Posted, int AlreadyPosted, int Unchanged, int Superseded, int Skipped, int Errors);

/// <summary>Result returned from a single entity import batch.</summary>
public sealed record ImportBatchResult(
    Guid BatchId,
    int RowCount,
    int ErrorCount,
    ImportOutcomeCounts Counts,
    IReadOnlyList<ImportBatchError> Errors);

/// <summary>A per-row error surfaced in the import batch result.</summary>
public sealed record ImportBatchError(int RowNumber, string Field, string Reason);

/// <summary>
/// Orchestrates entity import for one CSV upload (WP-3 Task 3.1). Parses the CSV via
/// <see cref="EntityImporter"/> for the given kind, creates Directory rows via existing Directory
/// commands dispatched through <see cref="ISender"/>, stages one <see cref="ImportBatch"/> +
/// its <see cref="ImportRow"/>s, and records each row's external-id → LeaseBook id mapping in
/// <c>ImportRow.MappedJson</c>. Runs entirely within the ambient RLS transaction; one bad row
/// never fails the whole batch.
/// </summary>
public sealed class EntityImportService(
    DbContext db,
    ISender sender,
    IActorContext actor,
    ExternalIdResolver resolver,
    ILogger<EntityImportService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ImportBatchResult> ImportAsync(
        EntityKind kind,
        string mappingProfile,
        string filename,
        Stream csvStream,
        CancellationToken ct)
    {
        var profile = AppFolioProfiles.For(kind);
        var rowOutcomes = new List<RowOutcome>();

        switch (kind)
        {
            case EntityKind.Owners:
                await ImportOwnersAsync(EntityImporter.ReadOwners(csvStream, profile), rowOutcomes, ct);
                break;
            case EntityKind.Properties:
                await ImportPropertiesAsync(EntityImporter.ReadProperties(csvStream, profile), rowOutcomes, ct);
                break;
            case EntityKind.Units:
                await ImportUnitsAsync(EntityImporter.ReadUnits(csvStream, profile), rowOutcomes, ct);
                break;
            case EntityKind.TenantsLeases:
                await ImportTenantsLeasesAsync(EntityImporter.ReadTenantsLeases(csvStream, profile), rowOutcomes, ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an entity kind.");
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
            var mappedJson = outcome.OverrideMappedJson
                ?? (outcome.IsError
                    ? JsonSerializer.Serialize(new { externalId = outcome.ExternalId, leaseBookId = (Guid?)null }, JsonOpts)
                    : JsonSerializer.Serialize(new { externalId = outcome.ExternalId, leaseBookId = outcome.LeaseBookId }, JsonOpts));

            var errorsJson = outcome.IsError
                ? JsonSerializer.Serialize(
                    new[] { new { field = outcome.ErrorField, reason = outcome.ErrorReason } }, JsonOpts)
                : null;

            db.Set<ImportRow>().Add(ImportRow.Create(
                batch.Id,
                outcome.RowNumber,
                outcome.RawJson,
                mappedJson,
                outcome.IsError ? "error" : "posted",
                errorsJson));
        }

        await db.SaveChangesAsync(ct);

        // RowOutcome only distinguishes success vs. error (entity creates have no already-posted,
        // unchanged, superseded, or skipped concept), so every non-error row counts as Posted and
        // the rest of the vocabulary stays at zero.
        var counts = new ImportOutcomeCounts(
            Posted: rowOutcomes.Count(r => !r.IsError),
            AlreadyPosted: 0,
            Unchanged: 0,
            Superseded: 0,
            Skipped: 0,
            Errors: totalErrors);

        return new ImportBatchResult(batch.Id, rowOutcomes.Count, totalErrors, counts, batchErrors);
    }

    // -------------------------------------------------------------------------
    // Per-kind import methods
    // -------------------------------------------------------------------------

    private async Task ImportOwnersAsync(
        ImportResult<OwnerRow> parsed,
        List<RowOutcome> outcomes,
        CancellationToken ct)
    {
        AddParseErrorOutcomes(parsed.Errors, outcomes);

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalId, row.Name, row.Reserve });
            Guid leaseBookId;
            try
            {
                leaseBookId = await sender.Send(
                    new CreateOwner(row.Name, null, null, null, null, row.Reserve), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    LogEvents.ImportRowFailed,
                    ex,
                    "Import row failed. Kind={EntityKind} RowNumber={RowNumber}",
                    "owners", rowNumber);

                outcomes.Add(RowOutcome.Error(
                    rowNumber,
                    row.ExternalId,
                    rawJson,
                    "*",
                    "This row could not be imported. Check the values and try again."));
                continue;
            }

            outcomes.Add(RowOutcome.Success(rowNumber, row.ExternalId, leaseBookId, rawJson));
        }
    }

    private async Task ImportPropertiesAsync(
        ImportResult<PropertyRow> parsed,
        List<RowOutcome> outcomes,
        CancellationToken ct)
    {
        AddParseErrorOutcomes(parsed.Errors, outcomes);

        var ownerMap = await resolver.BuildMapAsync(EntityKind.Owners, ct);

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalId, row.ExternalOwnerId, row.Address });

            if (!ownerMap.TryGetValue(row.ExternalOwnerId, out var ownerId))
            {
                outcomes.Add(RowOutcome.Error(rowNumber, row.ExternalId, rawJson,
                    "external_owner_id",
                    $"'{row.ExternalOwnerId}' not found in imported owners"));
                continue;
            }

            Guid leaseBookId;
            try
            {
                leaseBookId = await sender.Send(
                    new CreateProperty(ownerId, row.Address, null, null, null, null), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    LogEvents.ImportRowFailed,
                    ex,
                    "Import row failed. Kind={EntityKind} RowNumber={RowNumber}",
                    "properties", rowNumber);

                outcomes.Add(RowOutcome.Error(
                    rowNumber,
                    row.ExternalId,
                    rawJson,
                    "*",
                    "This row could not be imported. Check the values and try again."));
                continue;
            }

            outcomes.Add(RowOutcome.Success(rowNumber, row.ExternalId, leaseBookId, rawJson));
        }
    }

    private async Task ImportUnitsAsync(
        ImportResult<MigratorModel.UnitRow> parsed,
        List<RowOutcome> outcomes,
        CancellationToken ct)
    {
        AddParseErrorOutcomes(parsed.Errors, outcomes);

        var propertyMap = await resolver.BuildMapAsync(EntityKind.Properties, ct);

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new { row.ExternalId, row.ExternalPropertyId, row.Label, row.Rent, row.Status });

            if (!propertyMap.TryGetValue(row.ExternalPropertyId, out var propertyId))
            {
                outcomes.Add(RowOutcome.Error(rowNumber, row.ExternalId, rawJson,
                    "external_property_id",
                    $"'{row.ExternalPropertyId}' not found in imported properties"));
                continue;
            }

            var status = NormaliseUnitStatus(row.Status);
            Guid leaseBookId;
            try
            {
                leaseBookId = await sender.Send(
                    new CreateUnit(propertyId, row.Label, row.Rent, status), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    LogEvents.ImportRowFailed,
                    ex,
                    "Import row failed. Kind={EntityKind} RowNumber={RowNumber}",
                    "units", rowNumber);

                outcomes.Add(RowOutcome.Error(
                    rowNumber,
                    row.ExternalId,
                    rawJson,
                    "*",
                    "This row could not be imported. Check the values and try again."));
                continue;
            }

            outcomes.Add(RowOutcome.Success(rowNumber, row.ExternalId, leaseBookId, rawJson));
        }
    }

    private async Task ImportTenantsLeasesAsync(
        ImportResult<TenantLeaseRow> parsed,
        List<RowOutcome> outcomes,
        CancellationToken ct)
    {
        AddParseErrorOutcomes(parsed.Errors, outcomes);

        var unitMap = await resolver.BuildMapAsync(EntityKind.Units, ct);

        foreach (var (row, rowNumber) in WithSourceRowNumbers(parsed.Rows, parsed.Errors))
        {
            var rawJson = SerializeRaw(new
            {
                row.ExternalId,
                row.ExternalUnitId,
                row.DisplayName,
                row.StartDate,
                row.EndDate,
                row.Rent,
                row.DepositRequired,
                row.Status,
            });

            if (!unitMap.TryGetValue(row.ExternalUnitId, out var unitId))
            {
                outcomes.Add(RowOutcome.Error(rowNumber, row.ExternalId, rawJson,
                    "external_unit_id",
                    $"'{row.ExternalUnitId}' not found in imported units"));
                continue;
            }

            // Create Tenant first, then Lease.
            var tenantStatus = NormaliseTenantStatus(row.Status);
            Guid tenantId;
            try
            {
                tenantId = await sender.Send(
                    new CreateTenant(row.DisplayName, null, null, tenantStatus), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    LogEvents.ImportRowFailed,
                    ex,
                    "Import row failed. Kind={EntityKind} RowNumber={RowNumber}",
                    "tenants_leases", rowNumber);

                outcomes.Add(RowOutcome.Error(
                    rowNumber,
                    row.ExternalId,
                    rawJson,
                    "*",
                    "This row could not be imported. Check the values and try again."));
                continue;
            }

            var leaseStatus = NormaliseLeaseStatus(row.Status);
            Guid leaseId;
            try
            {
                leaseId = await sender.Send(
                    new CreateLease(tenantId, unitId, row.StartDate, row.EndDate,
                        row.Rent, row.DepositRequired, leaseStatus), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    LogEvents.ImportRowFailed,
                    ex,
                    "Import row failed. Kind={EntityKind} RowNumber={RowNumber}",
                    "tenants_leases", rowNumber);

                outcomes.Add(RowOutcome.Error(
                    rowNumber,
                    row.ExternalId,
                    rawJson,
                    "*",
                    "This row could not be imported. Check the values and try again."));
                continue;
            }

            // The lease id is the primary id; also store tenantId for 3.2 balance resolution.
            var mappedJson = JsonSerializer.Serialize(
                new { externalId = row.ExternalId, leaseBookId = leaseId, tenantId }, JsonOpts);

            outcomes.Add(new RowOutcome(rowNumber, row.ExternalId, leaseId, rawJson,
                mappedJson, IsError: false, null, null));
        }
    }

    // -------------------------------------------------------------------------
    // Shared utilities
    // -------------------------------------------------------------------------

    private static void AddParseErrorOutcomes(IReadOnlyList<RowError> parseErrors, List<RowOutcome> outcomes)
    {
        foreach (var e in parseErrors)
            outcomes.Add(RowOutcome.Error(e.RowNumber, string.Empty, "{}", e.Field, e.Reason));
    }

    /// <summary>
    /// Pairs each successfully-parsed row with its true 1-based source CSV row number. The parser
    /// returns valid rows and parse errors separately, with only the errors carrying their original
    /// position; this reconstructs each valid row's position by skipping the row numbers already
    /// claimed by parse errors, so persisted <see cref="ImportRow.RowNumber"/>s never collide with
    /// (or leave gaps around) the parse-error rows — the operator sees one consistent numbering.
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

    private static string NormaliseUnitStatus(string raw) => raw.ToLowerInvariant() switch
    {
        "occupied" or "rented" => "occupied",
        "unavailable" or "not available" or "offline" => "unavailable",
        _ => "vacant",
    };

    private static string NormaliseTenantStatus(string raw) => raw.ToLowerInvariant() switch
    {
        "late" => "late",
        "prepaid" => "prepaid",
        "evicting" => "evicting",
        "past" or "former" or "ended" => "past",
        _ => "current",
    };

    private static string NormaliseLeaseStatus(string raw) => raw.ToLowerInvariant() switch
    {
        "ended" or "past" or "former" => "ended",
        "pending" => "pending",
        _ => "active",
    };

    // -------------------------------------------------------------------------
    // Private outcome record for staging results before the batch id is known
    // -------------------------------------------------------------------------

    private sealed record RowOutcome(
        int RowNumber,
        string ExternalId,
        Guid? LeaseBookId,
        string RawJson,
        string? OverrideMappedJson, // non-null only for tenants+leases (stores extra fields)
        bool IsError,
        string? ErrorField,
        string? ErrorReason)
    {
        public static RowOutcome Success(int rowNumber, string externalId, Guid leaseBookId, string rawJson) =>
            new(rowNumber, externalId, leaseBookId, rawJson, null, false, null, null);

        public static RowOutcome Error(int rowNumber, string externalId, string rawJson, string field, string reason) =>
            new(rowNumber, externalId, null, rawJson, null, true, field, reason);
    }
}
