namespace LeaseBook.Modules.Reporting.Catalog;

/// <summary>
/// Static descriptor for one entry in the report catalog (§M5, screen-reports.jsx).
/// </summary>
/// <param name="Id">Stable kebab-case identifier (e.g. "owner-stmt").</param>
/// <param name="Name">Human display name.</param>
/// <param name="Category">Category tag: "Owner", "Trust accounting", or "Banking".</param>
/// <param name="Icon">Design-system icon name from screen-reports.jsx.</param>
/// <param name="Description">Short description for the catalog card.</param>
/// <param name="AcceptedFilters">
/// Filter keys this report accepts (e.g. ["year", "month", "owner", "property"]).
/// Used by the preview endpoint to validate incoming query params.
/// </param>
/// <param name="Favorite">Whether this report is starred in the prototype.</param>
public sealed record ReportDescriptor(
    string Id,
    string Name,
    string Category,
    string Icon,
    string Description,
    IReadOnlyList<string> AcceptedFilters,
    bool Favorite = false);
