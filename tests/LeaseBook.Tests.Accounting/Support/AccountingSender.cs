using LeaseBook.Modules.Accounting;
using LeaseBook.Modules.Accounting.Contracts;
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
/// dimensions and the organization's time zone, which the host reads from Directory, and the clock.
/// </summary>
internal static class AccountingSender
{
    public static ISender Create(
        DbContext db, IOrgContext org, IActorContext actor, ITenantPostingDimensions dimensions, TimeProvider? clock = null,
        string zone = OrgTimeZones.Default)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => db);
        services.AddSingleton(org);
        services.AddSingleton(actor);
        services.AddSingleton(dimensions);
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton<IOrgTimeZone>(new FixedOrgTimeZone(zone));
        services.AddAccountingModule();
        services.AddLeaseBookCqrs(typeof(AccountingModuleServiceCollectionExtensions).Assembly);
        return services.BuildServiceProvider().GetRequiredService<ISender>();
    }

    public static ISender Create(
        OrgScope scope, ITenantPostingDimensions dimensions, TimeProvider? clock = null, string zone = OrgTimeZones.Default) =>
        Create(scope.Db, scope.Tenant, scope.Actor, dimensions, clock, zone);
}

/// <summary>Each listed tenant has one lease, on these dimensions, on every date; any other tenant has none.</summary>
internal sealed class Leases(IReadOnlyDictionary<Guid, TenantPostingDimensions> leases) : ITenantPostingDimensions
{
    public Task<TenantPostingDimensions?> GetAsync(Guid tenantId, DateOnly date, CancellationToken ct) =>
        Task.FromResult(leases.GetValueOrDefault(tenantId));
}

/// <summary>A clock stopped at one instant.</summary>
internal sealed class StoppedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>The organization keeps its books in one listed zone.</summary>
internal sealed class FixedOrgTimeZone(string zone = OrgTimeZones.Default) : IOrgTimeZone
{
    public Task<TimeZoneInfo> GetAsync(CancellationToken ct) => Task.FromResult(OrgTimeZones.Resolve(zone));
}
