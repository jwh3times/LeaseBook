namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>Which part of the held-fees opening shape was violated (WP-7 §3.1).</summary>
public enum InvalidOpeningPositionReason
{
    /// <summary>Tagged to one basis; the trust equation's held_pm_fees term would not read it.</summary>
    HeldFeesBasisMustBeBoth,

    /// <summary>No bank dimension; the term is grouped per trust bank, so the fees would vanish.</summary>
    HeldFeesBankRequired,

    /// <summary>Carries an owner dimension, breaking the structural PM/owner isolation.</summary>
    PmIncomeOwnerDimension,

    /// <summary>The named bank has no trust_bank-class chart entry, so it sits outside the equation.</summary>
    HeldFeesBankNotTrust,
}

/// <summary>
/// A pm_income opening position violated the held-fees shape (WP-7 §3.1). Both wrong-shape
/// outcomes are I1-invisible (the entry still balances) and surface only later as an I2
/// variance — so the shape is enforced where it is created (S1). Messages are S2-clean
/// (no account codes / ids); the technical detail belongs at the caller's log site.
/// <para>
/// An <see cref="AccountingDomainException"/> so the host's handler maps it to a 409 by code, which
/// is load-bearing for the corrected re-import path: that caller must let it propagate (a reversal
/// and its revision have to commit together), and only an exception that escapes the endpoint
/// reaches <c>OrgContextMiddleware</c>'s rollback. Catching it in an endpoint to build the same 409
/// by hand would let the middleware commit the half-finished correction.
/// </para>
/// <para>
/// The reason discriminator carries the message rather than the thrower supplying it, matching
/// <see cref="InvalidLineException"/>. That is what puts these messages inside
/// <c>DomainExceptionMessageTests</c>'s reflective sweep — a free-form message parameter would let
/// the next one written leak an id with nothing to catch it.
/// </para>
/// </summary>
public sealed class InvalidOpeningPositionException(InvalidOpeningPositionReason reason)
    : AccountingDomainException(CodeOf(reason), Describe(reason))
{
    public InvalidOpeningPositionReason Reason { get; } = reason;

    /// <summary>The stable wire code; part of the error contract and asserted by callers.</summary>
    private static string CodeOf(InvalidOpeningPositionReason reason) => reason switch
    {
        InvalidOpeningPositionReason.HeldFeesBasisMustBeBoth => "held_fees_basis_must_be_both",
        InvalidOpeningPositionReason.HeldFeesBankRequired => "held_fees_bank_required",
        InvalidOpeningPositionReason.PmIncomeOwnerDimension => "pm_income_owner_dimension",
        InvalidOpeningPositionReason.HeldFeesBankNotTrust => "held_fees_bank_not_trust",
        _ => "invalid_opening_position",
    };

    private static string Describe(InvalidOpeningPositionReason reason) => reason switch
    {
        InvalidOpeningPositionReason.HeldFeesBasisMustBeBoth =>
            "A held-fees opening must apply to both accounting bases.",
        InvalidOpeningPositionReason.HeldFeesBankRequired =>
            "A held-fees opening must name the trust bank account holding the fees.",
        InvalidOpeningPositionReason.PmIncomeOwnerDimension =>
            "A held-fees opening cannot be attributed to an owner.",
        InvalidOpeningPositionReason.HeldFeesBankNotTrust =>
            "Held fees can only be imported into a trust bank account, not an operating account.",
        _ => "This opening position has an invalid shape.",
    };
}
