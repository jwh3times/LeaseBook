using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Time;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// The organization's time zone for every module that keeps its books (#642): Accounting, Operations
/// and Payments each declare the port, and Directory owns the setting.
/// </summary>
internal sealed class OrgTimeZoneAdapter(ISender sender)
    : LeaseBook.Modules.Accounting.Contracts.IOrgTimeZone,
        LeaseBook.Modules.Operations.Contracts.IOrgTimeZone,
        LeaseBook.Modules.Payments.Contracts.IOrgTimeZone
{
    public async Task<TimeZoneInfo> GetAsync(CancellationToken ct) =>
        OrgTimeZones.Resolve(await sender.Query(new GetOrgTimeZone(), ct));
}
