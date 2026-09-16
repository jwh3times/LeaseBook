using QuestPDF.Infrastructure;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Single place where QuestPDF's process-wide settings are pinned (M8, issue #396).
/// <para>
/// QuestPDF reads these statics once, when the first document is generated, so every render path
/// calls <see cref="Ensure"/> before building a document. <c>Program.cs</c> also calls it at host
/// startup; the call is idempotent, and the duplication covers CLI verbs, seeders and tests that
/// reach a renderer without going through the host pipeline.
/// </para>
/// <para>
/// Every value here is set <b>explicitly</b> rather than inherited from the library default. Three of
/// these defaults changed in QuestPDF 2026.9.0, and the previous defaults were what let a missing
/// glyph reach production as a blank space instead of a failed render. Pinning them means a future
/// default flip shows up as a diff on this file rather than as a silent change in what a statement
/// PDF looks like.
/// </para>
/// </summary>
internal static class QuestPdfSetup
{
    /// <summary>
    /// Applies the settings. Idempotent and safe to call from any thread: each assignment is a
    /// write of the same constant, so concurrent callers cannot observe a torn configuration.
    /// </summary>
    internal static void Ensure()
    {
        // Community tier (M5 WP-04). Free under the $1M annual revenue threshold; LeaseBook
        // qualifies at launch. Must be set before the first document is rendered.
        QuestPDF.Settings.License = LicenseType.Community;

        // Fonts come from the library's bundled Lato and nothing else. The application image is
        // `aspnet:10.0-noble-chiseled`, which carries no fonts at all, so anything that resolved
        // against the host font set would render one way in dev and another way in production.
        // Off in both places is the only setting that makes those two agree.
        QuestPDF.Settings.UseSystemFonts = false;

        // Fail the render rather than emit a document with holes in it. A statement that silently
        // drops a character is worse than one that does not generate: the caller still gets a
        // plausible-looking PDF, and nobody finds out until an owner reads it. These two are the
        // guard that issue #396 was filed against, so they are deliberately left on.
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = true;

        // Off on purpose, against the 2026.9.0 default. When enabled, a layout failure puts
        // fragments of the document into the exception message, and from there into logs — which
        // for these documents means owner names, tenant names and money. That is the same property
        // `DeliverTelemetryTests` pins for recipient email. Layout faults here are diagnosable from
        // the stack trace and the statement id already carried on the error contract (ADR-025).
        QuestPDF.Settings.EnableDetailedLayoutErrors = false;
    }
}
