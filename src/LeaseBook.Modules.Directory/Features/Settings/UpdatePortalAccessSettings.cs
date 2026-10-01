using System.Text.Json.Serialization;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Settings;

/// <summary>Admin-only permission change without resubmitting unrelated organization settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdatePortalAccessSettings(bool StaffCanManagePortalAccess) : ICommand<OrgSettingsResponse>;

internal sealed class UpdatePortalAccessSettingsHandler(DbContext db)
    : ICommandHandler<UpdatePortalAccessSettings, OrgSettingsResponse>
{
    public async Task<OrgSettingsResponse> Handle(UpdatePortalAccessSettings command, CancellationToken ct)
    {
        var settings = await db.Set<OrgSettings>().FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new OrgSettings { Id = UuidV7.NewId() };
            db.Add(settings);
        }
        settings.StaffCanManagePortalAccess = command.StaffCanManagePortalAccess;
        await db.SaveChangesAsync(ct);
        return OrgSettingsResponse.From(settings);
    }
}
