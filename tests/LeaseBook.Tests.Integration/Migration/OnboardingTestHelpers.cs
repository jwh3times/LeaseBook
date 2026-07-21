using System.Net;
using System.Net.Http.Json;
using LeaseBook.Web.Onboarding;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Integration.Migration;

/// <summary>
/// Shared HTTP + clearing helpers for the onboarding import/supersede integration tests. These are the
/// mechanical, self-contained pieces promoted out of <see cref="BalanceImportTests"/> so the supersede
/// suite reuses them verbatim rather than copy-pasting. (The entangled bits — <c>SetupAsync</c> and the
/// verification arrange — stay local to each class because they touch instance fixture state.)
/// </summary>
internal static class OnboardingTestHelpers
{
    public const string CutoverStr = "2026-06-30";

    /// <summary>Posts an entity import (owners/properties/units/tenants_leases) and asserts 200.</summary>
    public static async Task<T> PostImportAsync<T>(
        HttpClient client, string kind, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync($"/api/onboarding/import/{kind}", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    /// <summary>Posts a balance import (bank/owner/deposit/receivable) and asserts 200.</summary>
    public static async Task<T> PostBalanceImportAsync<T>(
        HttpClient client, string kind, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync($"/api/onboarding/import-balances/{kind}", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    /// <summary>
    /// Imports one owner (O-1 "Chain Owner LLC") → property (P-1) → unit (UNIT-1) → tenant/lease (T-1)
    /// chain via the entity-import endpoints, so owner/deposit/receivable balance rows can resolve.
    /// </summary>
    public static async Task ImportOwnerTenantChainAsync(HttpClient client, CancellationToken ct)
    {
        await PostImportAsync<ImportBatchResult>(client, "owners",
            new { csvContent = "Owner ID,Owner Name,Reserve\nO-1,Chain Owner LLC,0\n", filename = "owners.csv" }, ct);
        await PostImportAsync<ImportBatchResult>(client, "properties",
            new { csvContent = "Property ID,Owner ID,Address\nP-1,O-1,1 Chain St\n", filename = "properties.csv" }, ct);
        await PostImportAsync<ImportBatchResult>(client, "units",
            new { csvContent = "Unit ID,Property ID,Unit,Rent,Status\nUNIT-1,P-1,Unit A,1000.00,occupied\n", filename = "units.csv" }, ct);
        await PostImportAsync<ImportBatchResult>(client, "tenants_leases",
            new
            {
                csvContent = "Tenant ID,Unit ID,Tenant Name,Lease Start,Lease End,Rent,Deposit,Status\n" +
                             "T-1,UNIT-1,Chain Tenant,2025-01-01,,1000.00,500.00,active\n",
                filename = "tenants.csv",
            }, ct);
    }

    /// <summary>
    /// Queries the migration_clearing net for the given basis ('cash' or 'accrual'):
    /// SUM(debit - credit) FILTER (WHERE basis IN (basis, 'both')). Positive = net debit, negative =
    /// net credit, zero = balanced. (Verbatim from <see cref="BalanceImportTests"/>.)
    /// </summary>
    public static async Task<decimal> ClearingNetAsync(DbContext db, string basis, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ClearingNet>(
            $"""
            SELECT COALESCE(
                SUM(COALESCE(debit, 0) - COALESCE(credit, 0))
                    FILTER (WHERE basis IN ({basis}, 'both')),
                0
            ) AS net
            FROM journal_lines
            WHERE account_class = 'migration_clearing'
            """).ToListAsync(ct);

        return rows.Count == 0 ? 0m : rows[0].Net;
    }

    private sealed record ClearingNet(decimal Net);
}
