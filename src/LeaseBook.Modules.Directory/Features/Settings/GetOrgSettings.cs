using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Settings;

/// <summary>The org's settings (§C.4). Lazily get-or-creates the single row on first read (P46).</summary>
public sealed record GetOrgSettings : IQuery<OrgSettingsResponse>;

/// <summary>Wire shape for org settings — enums render as their snake_case text (the storage contract).</summary>
public sealed record OrgSettingsResponse(
    string AccountingBasis,
    string MoneyNegativeDisplay,
    string? LegalName,
    string? Address,
    string? City,
    string? State,
    string? Zip,
    string? Phone,
    string? LogoBlobRef)
{
    public static OrgSettingsResponse From(OrgSettings s) => new(
        AccountingBasisConverter.ToDb(s.AccountingBasis),
        MoneyNegativeDisplayConverter.ToDb(s.MoneyNegativeDisplay),
        s.LegalName, s.Address, s.City, s.State, s.Zip, s.Phone, s.LogoBlobRef);
}

internal sealed class GetOrgSettingsHandler(DbContext db) : IQueryHandler<GetOrgSettings, OrgSettingsResponse>
{
    public async Task<OrgSettingsResponse> Handle(GetOrgSettings query, CancellationToken ct)
    {
        var settings = await db.Set<OrgSettings>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            // Lazy get-or-create: defaults (cash / minus). The unique (org_id) index is the backstop if
            // two first-reads race (P46). Runs in the request's org transaction (org-stamped + committed).
            settings = new OrgSettings { Id = UuidV7.NewId() };
            db.Set<OrgSettings>().Add(settings);
            await db.SaveChangesAsync(ct);
        }

        return OrgSettingsResponse.From(settings);
    }
}
