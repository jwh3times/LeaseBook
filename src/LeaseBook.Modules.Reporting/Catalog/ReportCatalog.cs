namespace LeaseBook.Modules.Reporting.Catalog;

/// <summary>
/// The registry of all supported reports (§M5, screen-reports.jsx priority order). Stateless
/// singleton — <see cref="All"/> exposes the descriptors in the same order as the prototype's
/// <c>REPORTS</c> array (Owner → Trust accounting → Banking by category position), followed by the
/// WP-8 Compliance pack.
/// </summary>
public static class ReportCatalog
{
    // Filters common to several reports.
    private static readonly IReadOnlyList<string> YearMonth = ["year", "month"];
    private static readonly IReadOnlyList<string> YearMonthOwnerProperty = ["year", "month", "owner", "property"];
    private static readonly IReadOnlyList<string> YearMonthBank = ["year", "month", "bank"];
    private static readonly IReadOnlyList<string> AsOfDate = ["asOf"];
    private static readonly IReadOnlyList<string> None = [];

    // WP-8: the compliance pack is a trust-account × from/to range export (a ZIP, not a preview).
    // The filter keys are the literal query params the endpoint binds (GET /api/reports/compliance-pack).
    private static readonly IReadOnlyList<string> BankPeriodRange = ["bankAccountId", "from", "to"];

    /// <summary>
    /// All report descriptors in the prototype's priority order (screen-reports.jsx <c>REPORTS</c>
    /// array), plus the WP-8 Compliance pack. Tests assert each category has the correct reports.
    /// </summary>
    public static readonly IReadOnlyList<ReportDescriptor> All =
    [
        new("owner-stmt",    "Owner statement",             "Owner",           "owners",   "Complete fiduciary story per property & period",   YearMonthOwnerProperty, Favorite: true),
        new("owner-bal",     "All owner ending balances",   "Owner",           "dashboard","Every owner balance with per-bank breakdown",       YearMonth,              Favorite: true),
        new("trust-ledger",  "Trust account ledger",        "Trust accounting","doc",      "Full activity for any trust account",              YearMonthBank),
        new("bank-rec",      "Bank reconciliation",         "Banking",         "bank",     "Reconciliation detail with cleared status",         YearMonthBank,          Favorite: true),
        new("deposit-liab",  "Security deposit liability",  "Trust accounting","wallet",   "Held deposits by tenant — recognized on application", YearMonth),
        new("rent-roll",     "Rent roll",                   "Owner",           "building", "Units, tenants, rent & status portfolio-wide",      None),
        new("delinquency",   "Delinquency",                 "Banking",         "clock",    "Outstanding tenant balances by age",               AsOfDate),
        new("mgmt-fee",      "Management fee income",       "Trust accounting","reports",  "PM income — isolated from owner reporting",        YearMonth),
        new("compliance-pack","Trust compliance pack",      "Compliance",      "doc",      "Audit-ready ZIP — trust ledger, deposit register, reconciliations & audit log for a closed period", BankPeriodRange),
    ];

    /// <summary>Look up a descriptor by id. Returns null if not found.</summary>
    public static ReportDescriptor? Find(string id) =>
        All.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
