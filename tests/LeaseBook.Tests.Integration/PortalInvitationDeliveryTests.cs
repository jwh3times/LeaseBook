using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Portal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

// Dedicated database: another host's background worker must not race the controlled transport.
public sealed class PortalInvitationDeliveryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Failed_delivery_is_visible_retryable_and_never_writes_proof_or_transport_errors_to_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var delivery = new RecordingDelivery { Fail = true };
        await using var factory = new ApiFactory(fixture.AppConnectionString);
        await using var api = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPortalInvitationDelivery>();
            services.AddSingleton<IPortalInvitationDelivery>(delivery);
            foreach (var descriptor in services.Where(d => d.ImplementationType == typeof(PortalInvitationWorker)).ToArray())
                services.Remove(descriptor);
        }));
        var org = UuidV7.NewId();
        var target = UuidV7.NewId();
        var managerId = UuidV7.NewId();
        var managerEmail = $"delivery-manager-{org:N}@example.test";
        await AuthTestSupport.CreateOrgAsync(fixture, org, "Delivery test", ct);
        await using (var identityScope = api.Services.CreateAsyncScope())
        {
            var users = identityScope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser
            {
                Id = managerId,
                OrgId = org,
                UserName = managerEmail,
                Email = managerEmail,
                EmailConfirmed = true,
                DisplayName = "Manager"
            };
            (await users.CreateAsync(user, AuthTestSupport.DefaultPassword)).Succeeded.ShouldBeTrue();
            (await users.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }
        await InOrg(api.Services, org, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            db.Add(new Owner { Id = target, Name = "Invited owner" });
            await db.SaveChangesAsync(ct);
        }, ct);
        await Should.ThrowAsync<InvalidOperationException>(() => InOrg(api.Services, org, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            db.Add(new PortalInvitation
            {
                Id = UuidV7.NewId(),
                Persona = "owner",
                OwnerId = target,
                Email = "rollback@example.test",
                NormalizedEmail = "ROLLBACK@EXAMPLE.TEST",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(72),
                CreatedByUserId = managerId,
            });
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Roll back creation before delivery.");
        }, ct));
        await InOrg(api.Services, org, services => services.GetRequiredService<PortalInvitationDispatcher>().DispatchAsync(ct), ct);
        delivery.Attempts.ShouldBe(0, "rolled-back invitations cannot enter the delivery transport");
        using var manager = api.CreateClient();
        await manager.PrimeCsrfAsync(ct);
        await AuthTestSupport.LoginAsync(manager, managerEmail, ct);
        await manager.PrimeCsrfAsync(ct);
        var email = $"delivery-recipient-{org:N}@example.test";
        var created = await manager.PostAsJsonAsync($"/api/portal-access/owner/{target}", new { email }, ct);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        delivery.Messages.ShouldBeEmpty("creation commits before a separate delivery pass");

        await InOrg(api.Services, org, services => services.GetRequiredService<PortalInvitationDispatcher>().DispatchAsync(ct), ct);
        var failed = await manager.GetFromJsonAsync<JsonElement>($"/api/portal-access/owner/{target}", ct);
        failed.GetProperty("invitations")[0].GetProperty("deliveryStatus").GetString().ShouldBe("failed");

        delivery.Fail = false;
        (await manager.PostAsync($"/api/portal-access/invitations/{id}/retry", null, ct)).EnsureSuccessStatusCode();
        await InOrg(api.Services, org, services => services.GetRequiredService<PortalInvitationDispatcher>().DispatchAsync(ct), ct);
        var message = delivery.Messages.ShouldHaveSingleItem();
        message.Email.ShouldBe(email);
        message.InvitationId.ShouldBe(id);
        var delivered = await manager.GetFromJsonAsync<JsonElement>($"/api/portal-access/owner/{target}", ct);
        delivered.GetProperty("invitations")[0].GetProperty("deliveryStatus").GetString().ShouldBe("delivered");
        var token = new Uri(message.AcceptUrl).Fragment[7..];
        delivered.GetRawText().ShouldNotContain(token);

        await using var connection = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(connection, tx, org, ct);
        await using var command = new NpgsqlCommand("SELECT concat_ws(' ', before::text, after::text) FROM audit_events", connection, tx);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var auditRows = 0;
        while (await reader.ReadAsync(ct))
        {
            auditRows++;
            reader.GetString(0).ShouldNotContain(token);
            reader.GetString(0).ShouldNotContain(RecordingDelivery.SecretError);
        }
        auditRows.ShouldBeGreaterThan(0);
    }

    private static async Task InOrg(IServiceProvider root, Guid org, Func<IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = root.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(org, "delivery-test",
            () => work(scope.ServiceProvider), ct);
    }

    private sealed class RecordingDelivery : IPortalInvitationDelivery
    {
        public const string SecretError = "transport-secret-must-not-be-recorded";
        public bool Fail { get; set; }
        public int Attempts { get; private set; }
        public List<PortalTestMessage> Messages { get; } = [];
        public Task DeliverAsync(Guid orgId, PortalTestMessage message, CancellationToken ct)
        {
            Attempts++;
            if (Fail) throw new InvalidOperationException(SecretError + message.AcceptUrl);
            Messages.Add(message);
            return Task.CompletedTask;
        }
        public Task<PortalTestMessage?> ReadAsync(Guid orgId, Guid invitationId, CancellationToken ct) =>
            Task.FromResult(Messages.SingleOrDefault(m => m.InvitationId == invitationId));
    }
}
