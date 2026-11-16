using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #781: the tenant ledger's Add lease creates a lease through <c>POST /api/directory/leases</c>, and
/// creating one posts nothing. The deposit it names is a recorded amount only: collecting it stays
/// the ledger's deposit action, which is the posting. A lease is directory data, so the organization's
/// journal must be exactly as it was.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class LeaseCreationPostsNothingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Creating_a_lease_writes_no_journal_entry_or_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, client) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMStaff, ct);
        var (tenantId, unitId) = await TenantAndUnitAsync(orgId, ct);
        var entriesBefore = await OrgCountAsync(orgId, "journal_entries", ct);
        var linesBefore = await OrgCountAsync(orgId, "journal_lines", ct);

        var response = await client.PostAsJsonAsync("/api/directory/leases", new
        {
            tenantId,
            unitId,
            startDate = "2025-01-01",
            endDate = (string?)null,
            rent = 1395m,
            depositRequired = 1395m,
            status = "active",
        }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // The lease exists: the tenant now has it in effect, with the deposit it requires.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/directory/tenants/{tenantId}", ct);
        var lease = detail.GetProperty("lease");
        lease.GetProperty("unitId").GetGuid().ShouldBe(unitId);
        lease.GetProperty("depositRequired").GetDecimal().ShouldBe(1395m);
        // And nothing was posted for it: no charge, no deposit, nothing on any account.
        (await OrgCountAsync(orgId, "journal_entries", ct)).ShouldBe(entriesBefore);
        (await OrgCountAsync(orgId, "journal_lines", ct)).ShouldBe(linesBefore);
        detail.GetProperty("balance").GetDecimal().ShouldBe(0m);
        detail.GetProperty("depositHeld").GetDecimal().ShouldBe(0m);
    }

    /// <summary>
    /// A tenant and a vacant unit, with a trust account so the organization can post at all: an empty
    /// journal then means nothing was posted, not that nothing could be.
    /// </summary>
    private async Task<(Guid TenantId, Guid UnitId)> TenantAndUnitAsync(Guid orgId, CancellationToken ct)
    {
        Guid tenantId = default, unitId = default;
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
        {
            var ownerId = await sender.Send(new CreateOwner("Lease Owner", null, null, null, null, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(ownerId, "1 Lease St", "Raleigh", "NC", null, null), ct);
            unitId = await sender.Send(new CreateUnit(propertyId, "#1", 1350m, "available"), ct);
            tenantId = await sender.Send(new CreateTenant("Lease Tenant", null, null, "current"), ct);
            await sender.Send(new CreateBankAccount("Trust", null, null, "trust"), ct);
        }, ct);
        return (tenantId, unitId);
    }

    /// <summary>
    /// Counts the organization's rows as the superuser, which no RLS policy filters, so a count read
    /// without an organization context can't pass as a vacuous zero.
    /// </summary>
    private async Task<long> OrgCountAsync(Guid orgId, string table, CancellationToken ct)
    {
        var superuser = new NpgsqlConnectionStringBuilder(fixture.MigratorConnectionString)
        {
            Username = "postgres",
            Password = "dev_postgres_pw",
        }.ConnectionString;
        await using var conn = new NpgsqlConnection(superuser);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE org_id = @org", conn);
        cmd.Parameters.AddWithValue("org", orgId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
