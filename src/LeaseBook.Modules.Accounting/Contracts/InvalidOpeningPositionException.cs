namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// A pm_income opening position violated the held-fees shape (WP-7 §3.1). Both wrong-shape
/// outcomes are I1-invisible (the entry still balances) and surface only later as an I2
/// variance — so the shape is enforced where it is created (S1). Message is S2-clean
/// (no account codes / ids); the technical detail belongs at the caller's log site.
/// </summary>
public sealed class InvalidOpeningPositionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
