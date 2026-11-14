using LeaseBook.Modules.Accounting;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.SharedKernel.Time;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Tests.Accounting.Support;

/// <summary>
/// The real dispatcher over one context, wired the way the host wires it: the Accounting module's
/// services, every handler and validator in the module, and the validation decorator in front of each
/// handler. Only the seams the host fills from outside the module are stand-ins: the tenant's posting
/// dimensions, a property's owner on a date and each bank's purpose, which the host reads from
/// Directory, the organization's time zone, and the clock. Unless a test names owners, no property
/// belongs to anyone.
/// </summary>
internal static class AccountingSender
{
    public static ISender Create(
        DbContext db, IOrgContext org, IActorContext actor, ITenantPostingDimensions dimensions, TimeProvider? clock = null,
        string zone = SupportedTimeZones.Default, IPropertyOwnership? ownership = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => db);
        services.AddSingleton(org);
        services.AddSingleton(actor);
        services.AddSingleton(dimensions);
        services.AddSingleton(ownership ?? new PropertyOwners(new Dictionary<Guid, Guid>()));
        services.AddScoped<IBankAccountPurposes>(sp => new DirectoryBankPurposes(sp.GetRequiredService<DbContext>()));
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton<IOrgTimeZone>(new FixedOrgTimeZone(zone));
        services.AddAccountingModule();
        services.AddLeaseBookCqrs(typeof(AccountingModuleServiceCollectionExtensions).Assembly);
        return services.BuildServiceProvider().GetRequiredService<ISender>();
    }

    public static ISender Create(
        OrgScope scope, ITenantPostingDimensions dimensions, TimeProvider? clock = null, string zone = SupportedTimeZones.Default,
        IPropertyOwnership? ownership = null) =>
        Create(scope.Db, scope.Tenant, scope.Actor, dimensions, clock, zone, ownership);
}

/// <summary>Each listed tenant has one lease, on these dimensions, on every date; any other tenant has none.</summary>
internal sealed class Leases(IReadOnlyDictionary<Guid, TenantPostingDimensions> leases) : ITenantPostingDimensions
{
    public Task<TenantPostingDimensions?> GetAsync(Guid tenantId, DateOnly date, CancellationToken ct) =>
        Task.FromResult(leases.GetValueOrDefault(tenantId));
}

/// <summary>
/// Each listed property belongs to its listed owner on <paramref name="from"/> and every date after, and
/// to nobody before; any other property belongs to nobody.
/// </summary>
internal sealed class PropertyOwners(IReadOnlyDictionary<Guid, Guid> owners, DateOnly? from = null) : IPropertyOwnership
{
    public Task<Guid?> GetOwnerOnAsync(Guid propertyId, DateOnly date, CancellationToken ct) =>
        Task.FromResult(owners.TryGetValue(propertyId, out var owner) && (from is null || date >= from)
            ? owner
            : (Guid?)null);
}

/// <summary>The organization keeps its books in one listed zone.</summary>
internal sealed class FixedOrgTimeZone(string zone = SupportedTimeZones.Default) : IOrgTimeZone
{
    public Task<TimeZoneInfo> GetAsync(CancellationToken ct) => Task.FromResult(SupportedTimeZones.Resolve(zone));
}
