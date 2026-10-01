using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Settings;

/// <summary>
/// The org's settings (§C.4). Lazily get-or-creates the single row on first read (P46).
/// <para>
/// <paramref name="CreateIfMissing"/> false resolves a missing row to the same defaults without
/// writing it. That is the portal's read (#314, ADR-048): a tenant or owner persona holds no write
/// grant on <c>org_settings</c>, so the lazy insert would be rejected by the database — and a read
/// by someone outside the organization's staff should not be what materialises its settings anyway.
/// </para>
/// </summary>
public sealed record GetOrgSettings(bool CreateIfMissing = true) : IQuery<OrgSettingsResponse>;

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
    string? LogoBlobRef,
    // Late-fee org defaults (WP-6). Previously write-only: UpdateOrgSettings accepted all five but
    // the read side returned none, so no client could show the policy currently in force. Appended
    // rather than grouped with the profile fields to keep the positional record backward-compatible.
    int RentDueDay,
    int LateFeeGraceDays,
    string LateFeeKind,
    decimal LateFeeAmount,
    int LateFeeRateBps,
    bool StaffCanManagePortalAccess = true)
{
    public static OrgSettingsResponse From(OrgSettings s) => new(
        AccountingBasisConverter.ToDb(s.AccountingBasis),
        MoneyNegativeDisplayConverter.ToDb(s.MoneyNegativeDisplay),
        s.LegalName, s.Address, s.City, s.State, s.Zip, s.Phone, s.LogoBlobRef,
        s.RentDueDay,
        s.LateFeeGraceDays,
        LateFeeKindConverter.ToDb(s.LateFeeKind),
        s.LateFeeAmount,
        s.LateFeeRateBps,
        s.StaffCanManagePortalAccess);
}

internal sealed class GetOrgSettingsHandler(DbContext db) : IQueryHandler<GetOrgSettings, OrgSettingsResponse>
{
    public async Task<OrgSettingsResponse> Handle(GetOrgSettings query, CancellationToken ct)
    {
        var settings = await db.Set<OrgSettings>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null && !query.CreateIfMissing)
        {
            // The defaults the lazy insert below would have persisted, unpersisted.
            return OrgSettingsResponse.From(new OrgSettings());
        }

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
