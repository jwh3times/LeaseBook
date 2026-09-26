using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Portal;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Seeding;

/// <summary>Disposable resident demonstration, separate from every financial golden fixture.</summary>
public static class PortalSeeder
{
    public static readonly Guid PortalOrgId = new("01923000-0000-7000-8000-000000904001");
    public static readonly Guid OtherOrgId = new("01923000-0000-7000-8000-000000904002");
    public const string Password = "Portal-Fixture-2026!";
    public const string ResidentAEmail = "resident-a@portal.test";
    public const string ResidentBEmail = "resident-b@portal.test";
    public const string ResidentCEmail = "resident-c@portal.test";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        SeederGuard.RequireNonProduction(services);
        await SeedOrgAsync(services, PortalOrgId, [ResidentAEmail, ResidentBEmail], ct);
        await SeedOrgAsync(services, OtherOrgId, [ResidentCEmail], ct);
    }

    private static async Task SeedOrgAsync(IServiceProvider services, Guid orgId, string[] emails, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await RoleSeeder.EnsureRolesAsync(sp, ct);
        var db = sp.GetRequiredService<AppDbContext>();
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        await sp.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(orgId, "seed:portal", async () =>
        {
            if (await db.Orgs.AnyAsync(o => o.Id == orgId, ct)) { return; }
            db.Orgs.Add(new Org { Id = orgId, Name = "Portal Fixture" });
            var owner = new Owner { Id = UuidV7.NewId(), Name = "Fixture owner" };
            var property = new Property { Id = UuidV7.NewId(), OwnerId = owner.Id, Address = "1 Fixture Lane" };
            var bank = new BankAccount
            {
                Id = UuidV7.NewId(),
                Name = "Fixture trust",
                Institution = "Fixture bank",
                Mask = "0001",
                Purpose = Modules.Directory.Domain.BankPurpose.Trust,
            };
            db.AddRange(owner, property, bank);
            await db.SaveChangesAsync(ct);
            await sp.GetRequiredService<IChartOfAccounts>().ProvisionAsync(
                [new BankAccountSpec(bank.Id, bank.Name, Modules.Accounting.Contracts.BankPurpose.Trust)], ct);
            var events = sp.GetRequiredService<IAccountingEvents>();
            var sender = sp.GetRequiredService<ISender>();
            for (var i = 0; i < emails.Length; i++)
            {
                var name = emails[i] == ResidentAEmail ? "Resident A" : emails[i] == ResidentBEmail ? "Resident B" : "Resident C";
                var tenant = new Tenant { Id = UuidV7.NewId(), DisplayName = name };
                db.Add(tenant);
                await db.SaveChangesAsync(ct);
                var user = new AppUser
                {
                    Id = UuidV7.NewId(),
                    OrgId = orgId,
                    UserName = emails[i],
                    Email = emails[i],
                    DisplayName = name,
                    EmailConfirmed = true,
                };
                AccountSecurityAudit.RequireSuccess(await users.CreateAsync(user, Password));
                AccountSecurityAudit.RequireSuccess(await users.AddToRoleAsync(user, Roles.Tenant));
                await sp.GetRequiredService<ResidentAccessService>().GrantAsync(user.Id, tenant.Id, ct);
                var date = new DateOnly(2026, 9, 1);
                await events.PostAsync(new RentCharged(tenant.Id, property.Id, owner.Id, null,
                    new Money(1000m + i * 200m), date, "Internal rent memo", "fixture-rent-" + i), ct);
                await events.PostAsync(new PaymentReceived(tenant.Id, property.Id, owner.Id,
                    new Money(300m), date.AddDays(1), PaymentMethod.Check, bank.Id, "Internal bank reference", "fixture-payment-" + i), ct);
                var fee = await events.PostAsync(new FeeCharged(tenant.Id, property.Id, owner.Id, null,
                    new Money(25m), date.AddDays(2), FeeKind.Other, "Internal fee memo", "fixture-fee-" + i), ct);
                await sender.Send(new VoidEntry(fee, "Internal correction reason", date.AddDays(3), "fixture-void-" + i), ct);
            }
        }, ct);
    }
}
