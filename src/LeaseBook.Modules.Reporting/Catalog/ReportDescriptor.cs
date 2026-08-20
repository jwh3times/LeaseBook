namespace LeaseBook.Modules.Reporting.Catalog;

/// <summary>
/// Static descriptor for one entry in the report catalog (§M5, screen-reports.jsx).
/// </summary>
/// <param name="Id">Stable kebab-case identifier (e.g. "owner-stmt").</param>
/// <param name="Name">Human display name.</param>
/// <param name="Category">Category tag: "Owner", "Trust accounting", or "Banking".</param>
/// <param name="Icon">Design-system icon name from screen-reports.jsx.</param>
/// <param name="Description">Short description for the catalog card.</param>
/// <param name="FilterControls">
/// The filter controls the report builder should offer for this report, keyed by the literal query
/// parameter each one binds to (e.g. ["year", "month", "ownerId", "propertyId"]).
/// <para>
/// This is a UI affordance contract, not a validation gate: nothing server-side reads it, and the
/// preview endpoint binds its query parameters regardless of what this list says. The SPA tests
/// membership against these exact strings to decide which chips to render, so a key that does not
/// match a bound parameter name silently hides a control. <c>FilterControlVocabularyTests</c>
/// enforces the match.
/// </para>
/// </param>
/// <param name="Favorite">Whether this report is starred in the prototype.</param>
public sealed record ReportDescriptor(
    string Id,
    string Name,
    string Category,
    string Icon,
    string Description,
    IReadOnlyList<string> FilterControls,
    bool Favorite = false);
