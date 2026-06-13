namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// How negative money is rendered org-wide (§C.1, P46). <see cref="Minus"/> is first so it is the CLR
/// default, matching the <c>org_settings.money_negative_display DEFAULT 'minus'</c> store default. The
/// SPA's <c>&lt;Money&gt;</c> / <c>formatMoney</c> read this preference (M2-E10).
/// </summary>
public enum MoneyNegativeDisplay
{
    Minus,
    Parens,
}
