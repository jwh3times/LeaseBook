using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.Modules.Reporting.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007/016) for Reporting's <see cref="IPmBranding"/> port. Delegates to
/// Directory's <see cref="GetOrgSettings"/> query via <see cref="ISender"/>. Projects only the
/// narrow fields the Reporting module needs — company name, logo blob ref, and the
/// parenthesized-negatives preference (from <c>MoneyNegativeDisplay</c>).
/// </summary>
internal sealed class PmBrandingAdapter(ISender sender) : IPmBranding
{
    public async Task<PmBrandingRow> GetAsync(CancellationToken ct)
    {
        var settings = await sender.Query(new GetOrgSettings(), ct);

        // MoneyNegativeDisplay storage values: "minus" (−1,234.56) or "parens" ((1,234.56)).
        // "parens" is the NC fiduciary convention; map it to true.
        var parenthesized = string.Equals(
            settings.MoneyNegativeDisplay, "parens", StringComparison.OrdinalIgnoreCase);

        return new PmBrandingRow(settings.LegalName, settings.LogoBlobRef, parenthesized);
    }
}
