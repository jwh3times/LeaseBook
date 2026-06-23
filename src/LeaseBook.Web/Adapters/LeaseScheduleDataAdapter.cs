using LeaseBook.Modules.Directory.Features.Reporting;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel.Cqrs;
using DirRow = LeaseBook.Modules.Directory.Features.Reporting.LeaseScheduleRow;
using OpsRow = LeaseBook.Modules.Operations.Contracts.LeaseScheduleRow;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / ADR-019) for the Operations module's <see cref="ILeaseScheduleData"/>
/// port. Dispatches the Directory <see cref="GetActiveLeaseSchedule"/> query via
/// <see cref="ISender"/> and maps the response to Operations-owned <see cref="OpsRow"/> DTOs
/// (no cross-module type bleed into Modules.Operations).
/// </summary>
internal sealed class LeaseScheduleDataAdapter(ISender sender) : ILeaseScheduleData
{
    public async Task<IReadOnlyList<OpsRow>> GetActiveAsync(
        int year, int month, CancellationToken ct)
    {
        var response = await sender.Query(new GetActiveLeaseSchedule(year, month), ct);

        return response.Rows
            .Select(r => new OpsRow(
                r.LeaseId,
                r.TenantId,
                r.PropertyId,
                r.OwnerId,
                r.UnitId,
                r.TenantName,
                r.UnitLabel,
                r.Rent,
                r.StartDate,
                r.EndDate))
            .ToList();
    }
}
