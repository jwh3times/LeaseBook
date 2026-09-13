using LeaseBook.Modules.Reporting.Contracts;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// The presentation-layer view of one owner statement (§M5): the Accounting statement data
/// enriched with display names, PM branding, the fiduciary tie-out flags, and the optional
/// "reconciles-to" bank snapshot. <b>No financial math here</b> — every figure comes verbatim from
/// the Accounting engine. Lives in the host because it references types from both Accounting and
/// Reporting (the legitimate composition root per ADR-016).
/// </summary>
public sealed record StatementView(
    /// <summary>Owner id from the underlying Accounting statement.</summary>
    Guid OwnerId,
    /// <summary>Owner display name resolved via <see cref="IStatementNames"/>.</summary>
    string OwnerName,
    /// <summary>The property the statement is scoped to, or null for a portfolio-wide statement.</summary>
    Guid? PropertyId,
    /// <summary>Filtered property address, or null for a portfolio-wide statement.</summary>
    string? PropertyAddress,
    /// <summary>Accounting basis: "cash" or "accrual".</summary>
    string Basis,
    int Year,
    int Month,
    decimal Beginning,
    /// <summary>
    /// How <see cref="Beginning"/> follows from the statement issued for the preceding period (ADR-045).
    /// Null when no such statement exists or nothing has changed since it was issued.
    /// </summary>
    CarryForwardView? CarryForward,
    IReadOnlyList<StatementSectionView> Sections,
    decimal Ending,
    /// <summary>Fiduciary panel flags from the Accounting tie-out; variance must be zero to display.</summary>
    FiduciaryPanel Fiduciary,
    /// <summary>PM branding to render in the statement header.</summary>
    PmBrandingRow Branding,
    /// <summary>The instant (UTC) the statement's figures were read; stored when it is issued.</summary>
    DateTime AsOf);

/// <summary>
/// The prior-period adjustments section (ADR-045): the ending balance the preceding period's issued
/// statement presented, what was posted into that period or earlier after it was issued, and the
/// result — which is always this statement's <see cref="StatementView.Beginning"/>.
/// <c>IssuedEnding + Total = Beginning</c> and <c>Σ Lines + Unitemized = Total</c>.
/// </summary>
/// <param name="IssuedAt">When the anchoring statement was issued (UTC).</param>
/// <param name="Unitemized">
/// The part of <see cref="Total"/> no posted-after entry explains. Zero in practice; rendered as its own
/// labelled line when it is not, never folded into another figure.
/// </param>
public sealed record CarryForwardView(
    int IssuedYear,
    int IssuedMonth,
    DateTime IssuedAt,
    decimal IssuedEnding,
    IReadOnlyList<CarryForwardLineView> Lines,
    decimal Unitemized,
    decimal Total);

/// <summary>
/// One prior-period adjustment: an entry dated into an already-issued period, and when it was actually
/// posted. The pair of dates is the audit trail between the two statements.
/// </summary>
public sealed record CarryForwardLineView(
    Guid EntryId,
    DateOnly Date,
    DateTime PostedAt,
    string EventType,
    string Description,
    string? PropertyAddress,
    decimal Amount);

/// <summary>A statement section with its title and line items.</summary>
public sealed record StatementSectionView(
    string Key,
    string Title,
    IReadOnlyList<StatementLineView> Lines,
    decimal Subtotal);

/// <summary>A single line item in a statement section.</summary>
public sealed record StatementLineView(
    Guid EntryId,
    DateOnly Date,
    string EventType,
    string? EventSubtype,
    string Description,
    string? PropertyAddress,
    decimal Amount);

/// <summary>
/// Fiduciary panel flags surfaced on the statement footer (NC 58A .0116 transparency).
/// All must pass before a statement is considered presentable.
/// </summary>
/// <param name="Balanced">True when the Accounting tie-out variance is exactly zero.</param>
/// <param name="Variance">The tie-out variance (should be 0; exposed for diagnostic display).</param>
/// <param name="PmIncomeExcluded">True: PM income lines are structurally excluded from owner equity.</param>
/// <param name="DepositsRecognizedOnApplication">True: deposits appear only when applied (income recognition).</param>
/// <param name="LatestReconciledBank">The most recent finalized bank reconciliation, if any.</param>
public sealed record FiduciaryPanel(
    bool Balanced,
    decimal Variance,
    bool PmIncomeExcluded,
    bool DepositsRecognizedOnApplication,
    ReconciliationSnapshotRow? LatestReconciledBank);
