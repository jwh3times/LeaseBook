using System.Security.Cryptography;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Portal;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Payments;

public static class PaymentFixtureBootstrap
{
    public const string Password = "Payment-Fixture-2026!";

    /// <summary>
    /// Writes a manifest and seeds its fixtures. With no connected account, two simulator fixtures.
    /// With some, a Stripe sandbox manifest: one fixture organization per account. The Stripe key is
    /// never written; it stays in local secrets.
    /// </summary>
    public static async Task CreateManifestAsync(IServiceProvider services, string path, IReadOnlyList<string> stripeAccounts, CancellationToken ct)
    {
        RequireDevelopment(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!db.Database.GetDbConnection().Database.StartsWith("leasebook_payment_fixture", StringComparison.Ordinal)
            || await db.Orgs.AnyAsync(ct))
        { throw new InvalidOperationException("Initialize only an empty, migrated leasebook_payment_fixture database."); }
        var manifest = Manifest(stripeAccounts);
        var bindings = manifest.Fixtures;
        // CreateNew refuses overwriting a key/identity manifest. A failed bootstrap is discarded by
        // recreating this dedicated disposable database, never by deleting journal rows.
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        { await JsonSerializer.SerializeAsync(file, new { Payments = manifest }, new JsonSerializerOptions { WriteIndented = true }, ct); }
        for (var i = 0; i < bindings.Length; i++)
        { await SeedAsync(services, bindings[i], ((char)('a' + i)).ToString(), ct); }
    }

    // What a manifest holds: new identities, a new signing key, and the mode its accounts belong to.
    internal static SimulationSettings Manifest(IReadOnlyList<string> stripeAccounts) => new()
    {
        Mode = stripeAccounts.Count > 0 ? PaymentModes.StripeSandbox : PaymentModes.Simulation,
        Fixtures = [.. (stripeAccounts.Count > 0 ? stripeAccounts : [.. Enumerable.Range(0, 2).Select(_ => "sim_" + UuidV7.NewId().ToString("N"))])
            .Select(account => new FixtureBinding(UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId(), account))],
        SigningKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
    };

    // Shared by the CLI and integration harness. Every call creates fresh identities and refuses an
    // existing organization; a marker cannot turn a customer org into a simulation fixture.
    public static async Task SeedAsync(IServiceProvider services, FixtureBinding binding, string suffix, CancellationToken ct)
    {
        RequireDevelopment(services);
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await RoleSeeder.EnsureRolesAsync(sp, ct);
        var db = sp.GetRequiredService<AppDbContext>();
        await sp.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(binding.OrgId, "seed:payment-fixture", async () =>
        {
            if (await db.Orgs.AnyAsync(x => x.Id == binding.OrgId, ct))
            { throw new InvalidOperationException("A payment fixture must use a new organization."); }
            db.Add(new Org { Id = binding.OrgId, Name = "Payment simulation fixture " + suffix });
            var owner = new Owner { Id = UuidV7.NewId(), Name = "Simulation owner" };
            var property = new Property { Id = UuidV7.NewId(), OwnerId = owner.Id, Address = "1 Simulation Lane" };
            var bank = new BankAccount { Id = binding.BankId, Name = "Simulation trust", Purpose = Modules.Directory.Domain.BankPurpose.Trust };
            db.AddRange(owner, property, bank);
            await db.SaveChangesAsync(ct);
            await sp.GetRequiredService<IChartOfAccounts>().ProvisionAsync(
                [new BankAccountSpec(bank.Id, bank.Name, Modules.Accounting.Contracts.BankPurpose.Trust)], ct);
            db.Add(new PaymentFixture { Id = binding.Generation, BankId = binding.BankId, Account = binding.Account });
            await db.SaveChangesAsync(ct);
            var today = DateOnly.FromDateTime(sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
            for (var i = 1; i <= 2; i++)
            {
                var tenant = new Tenant { Id = UuidV7.NewId(), DisplayName = $"Simulation tenant {suffix}{i}" };
                var unit = new Unit { Id = UuidV7.NewId(), PropertyId = property.Id, Label = i.ToString(), Rent = new Money(1000m) };
                db.AddRange(tenant, unit);
                db.Add(new LeaseLite
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.Id,
                    UnitId = unit.Id,
                    StartDate = today.AddYears(-1),
                    EndDate = today.AddYears(1),
                    Status = LeaseStatus.Active,
                    Rent = new Money(1000m)
                });
                await db.SaveChangesAsync(ct);
                var user = await User(sp, binding.OrgId, $"tenant-{suffix}{i}@payments.test", Roles.Tenant);
                await sp.GetRequiredService<ResidentAccessService>().GrantAsync(user.Id, tenant.Id, ct);
                await sp.GetRequiredService<IAccountingEvents>().PostAsync(new RentCharged(tenant.Id, property.Id,
                    owner.Id, unit.Id, new Money(1000m), today, "Simulation rent", "simulation-rent-" + i), ct);
            }
            await User(sp, binding.OrgId, $"admin-{suffix}@payments.test", Roles.PMAdmin);
        }, ct);
    }

    private static async Task<AppUser> User(IServiceProvider sp, Guid orgId, string email, string role)
    {
        var manager = sp.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            Id = UuidV7.NewId(),
            OrgId = orgId,
            Email = email,
            UserName = email,
            DisplayName = "Simulation account",
            EmailConfirmed = true
        };
        AccountSecurityAudit.RequireSuccess(await manager.CreateAsync(user, Password));
        AccountSecurityAudit.RequireSuccess(await manager.AddToRoleAsync(user, role));
        return user;
    }

    private static void RequireDevelopment(IServiceProvider sp)
    {
        if (!sp.GetRequiredService<IHostEnvironment>().IsDevelopment())
        { throw new InvalidOperationException("Payment fixtures require Development."); }
    }
}
