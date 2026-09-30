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
using LeaseBook.Web.Reporting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Seeding;

/// <summary>
/// Disposable resident and owner portal demonstration, separate from every financial golden fixture.
/// The portal org has two resident logins and two owner logins (each owner with a property, activity, a
/// disbursement and an issued statement); the other org has one of each, for cross-org cases.
/// </summary>
public static class PortalSeeder
{
    public static readonly Guid PortalOrgId = new("01923000-0000-7000-8000-000000904001");
    public static readonly Guid OtherOrgId = new("01923000-0000-7000-8000-000000904002");
    public const string Password = "Portal-Fixture-2026!";
    public const string ResidentAEmail = "resident-a@portal.test";
    public const string ResidentBEmail = "resident-b@portal.test";
    public const string ResidentCEmail = "resident-c@portal.test";
    public const string OwnerAEmail = "owner-a@portal.test";
    public const string OwnerBEmail = "owner-b@portal.test";
    public const string OwnerCEmail = "owner-c@portal.test";
    public const string DepositBankMask = "9876";

    /// <summary>
    /// Prefix of every staff-only internal note the fixture writes (#468). No owner- or resident-facing
    /// surface may ever show it; the portal suites scan for it (and for "Internal") in every response.
    /// </summary>
    public const string StaffNoteMarker = "STAFF-ONLY note";

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
            var ownerEmail = orgId == PortalOrgId ? OwnerAEmail : OwnerCEmail;
            var owner = new Owner
            {
                Id = UuidV7.NewId(),
                Name = orgId == PortalOrgId ? "Owner A" : "Owner C",
                ContactEmail = ownerEmail,
            };
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
                var resident = await CreateLoginAsync(users, orgId, emails[i], name, Roles.Tenant);
                await sp.GetRequiredService<ResidentAccessService>().GrantAsync(resident, tenant.Id, ct);
                var date = new DateOnly(2026, 9, 1);
                await events.PostAsync(new RentCharged(tenant.Id, property.Id, owner.Id, null,
                    new Money(1000m + i * 200m), date, "Internal rent memo", "fixture-rent-" + i,
                    InternalNote: StaffNoteMarker + ": rent"), ct);
                await events.PostAsync(new PaymentReceived(tenant.Id, property.Id, owner.Id,
                    new Money(300m), date.AddDays(1), PaymentMethod.Check, bank.Id, "Internal bank reference", "fixture-payment-" + i,
                    InternalNote: StaffNoteMarker + ": payment"), ct);
                var fee = await events.PostAsync(new FeeCharged(tenant.Id, property.Id, owner.Id, null,
                    new Money(25m), date.AddDays(2), FeeKind.Other, "Internal fee memo", "fixture-fee-" + i,
                    InternalNote: StaffNoteMarker + ": fee"), ct);
                await sender.Send(new VoidEntry(fee, "Internal correction reason", date.AddDays(3), "fixture-void-" + i), ct);
            }

            // Owner portal (#464). Posted after the residents so their tenant ledgers are untouched: none
            // of these events names a resident tenant.
            var ownerLogin = await CreateLoginAsync(users, orgId, ownerEmail, owner.Name, Roles.Owner);
            await sp.GetRequiredService<OwnerAccessService>().GrantAsync(ownerLogin, owner.Id, ct);
            var issue = new List<(Guid OwnerId, Guid? PropertyId, string Email)> { (owner.Id, null, ownerEmail) };
            var september = new DateOnly(2026, 9, 1);
            if (orgId == PortalOrgId)
            {
                // A tenant deposit attributed to Owner A: a liability the portal balance must exclude.
                var depositBank = new BankAccount
                {
                    Id = UuidV7.NewId(),
                    Name = "Fixture deposit trust",
                    Institution = "Fixture bank",
                    Mask = DepositBankMask,
                    Purpose = Modules.Directory.Domain.BankPurpose.Deposit,
                };
                var depositor = new Tenant { Id = UuidV7.NewId(), DisplayName = "Deposit resident" };
                db.AddRange(depositBank, depositor);
                await db.SaveChangesAsync(ct);
                await sp.GetRequiredService<IChartOfAccounts>().ProvisionAsync(
                    [new BankAccountSpec(depositBank.Id, depositBank.Name, Modules.Accounting.Contracts.BankPurpose.Deposit)], ct);
                await events.PostAsync(new DepositCollected(depositor.Id, property.Id, owner.Id,
                    new Money(1234.56m), september.AddDays(3), depositBank.Id, "Internal deposit memo", "fixture-deposit",
                    InternalNote: StaffNoteMarker + ": deposit"), ct);

                await events.PostAsync(new OwnerContribution(owner.Id, property.Id, new Money(500m),
                    september.AddDays(4), bank.Id, "Internal owner contribution memo", "fixture-owner-a-contribution"), ct);
                await events.PostAsync(new ManagementFeeAssessed(owner.Id, property.Id, new Money(60m),
                    september.AddDays(5), bank.Id, "Internal management fee memo", "fixture-owner-a-fee"), ct);
                await events.PostAsync(new OwnerDisbursed(owner.Id, new Money(400m), september.AddDays(9), bank.Id,
                    "Internal disbursement memo", "fixture-owner-a-disbursement"), ct);
                var voided = await events.PostAsync(new OwnerDisbursed(owner.Id, new Money(150m), september.AddDays(11),
                    bank.Id, "Internal disbursement memo", "fixture-owner-a-disbursement-2"), ct);
                await sender.Send(new VoidEntry(voided, "Internal disbursement void reason", september.AddDays(12),
                    "fixture-owner-a-disbursement-2-void"), ct);
                issue.Add((owner.Id, property.Id, ownerEmail));

                var ownerB = new Owner { Id = UuidV7.NewId(), Name = "Owner B", ContactEmail = OwnerBEmail };
                var propertyB = new Property { Id = UuidV7.NewId(), OwnerId = ownerB.Id, Address = "2 Fixture Court" };
                db.AddRange(ownerB, propertyB);
                await db.SaveChangesAsync(ct);
                var ownerBUser = await CreateLoginAsync(users, orgId, OwnerBEmail, ownerB.Name, Roles.Owner);
                await sp.GetRequiredService<OwnerAccessService>().GrantAsync(ownerBUser, ownerB.Id, ct);
                await events.PostAsync(new OwnerContribution(ownerB.Id, propertyB.Id, new Money(2000m),
                    september.AddDays(4), bank.Id, "Internal owner contribution memo", "fixture-owner-b-contribution"), ct);
                await events.PostAsync(new OwnerDisbursed(ownerB.Id, new Money(750m), september.AddDays(19), bank.Id,
                    "Internal disbursement memo", "fixture-owner-b-disbursement"), ct);
                issue.Add((ownerB.Id, null, OwnerBEmail));
            }
            else
            {
                await events.PostAsync(new OwnerDisbursed(owner.Id, new Money(100m), september.AddDays(19), bank.Id,
                    "Internal disbursement memo", "fixture-owner-c-disbursement"), ct);
            }

            // Issue each statement through the real seam: assembly, tie-out gate, PDF, immutable artifact.
            var assembler = sp.GetRequiredService<StatementAssembler>();
            var delivery = sp.GetRequiredService<IStatementDelivery>();
            foreach (var (ownerId, propertyId, email) in issue)
            {
                var views = await assembler.BuildAsync([ownerId], propertyId, september.Year, september.Month, "cash", ct);
                await delivery.DeliverAsync(views[0], email, ct);
            }
        }, ct);
    }

    private static async Task<Guid> CreateLoginAsync(
        UserManager<AppUser> users, Guid orgId, string email, string name, string role)
    {
        var user = new AppUser
        {
            Id = UuidV7.NewId(),
            OrgId = orgId,
            UserName = email,
            Email = email,
            DisplayName = name,
            EmailConfirmed = true,
        };
        AccountSecurityAudit.RequireSuccess(await users.CreateAsync(user, Password));
        AccountSecurityAudit.RequireSuccess(await users.AddToRoleAsync(user, role));
        return user.Id;
    }
}
