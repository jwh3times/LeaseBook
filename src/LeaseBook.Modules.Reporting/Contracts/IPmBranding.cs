namespace LeaseBook.Modules.Reporting.Contracts;

/// <summary>
/// Consumer-owned read port (ADR-007/016): PM-facing branding/display settings for report headers.
/// The host adapter delegates to Directory's <c>GetOrgSettings</c> query via <c>ISender</c>.
/// Exposes only the narrow subset the Reporting module needs — company name, optional logo blob
/// reference, and the money-negative display preference for statement rendering.
/// </summary>
public interface IPmBranding
{
    Task<PmBrandingRow> GetAsync(CancellationToken ct);
}

/// <summary>PM branding row returned by <see cref="IPmBranding"/>.</summary>
/// <param name="CompanyName">The org's legal / trading name (or null if not yet configured).</param>
/// <param name="LogoBlobRef">Optional Azure Blob reference for the PM's logo.</param>
/// <param name="ParenthesizedNegatives">
/// When true, negative money values render as (1,234.56) instead of −1,234.56 — the NC fiduciary
/// convention. Sourced from <c>MoneyNegativeDisplay</c> in org settings.
/// </param>
public sealed record PmBrandingRow(string? CompanyName, string? LogoBlobRef, bool ParenthesizedNegatives);
