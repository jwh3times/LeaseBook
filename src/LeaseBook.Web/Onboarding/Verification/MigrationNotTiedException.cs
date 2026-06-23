namespace LeaseBook.Web.Onboarding.Verification;

/// <summary>
/// Thrown by <see cref="VerificationService.SignOffAsync"/> when the tie-out — recomputed against the
/// *current* journal at sign-off time — does not hold. Mapped to HTTP 409 <c>not_tied</c> at the
/// sign-off endpoint.
/// <para>
/// Tie-out fails if either the external match (variance ≠ 0) OR the internal clearing consistency
/// (clearing ≠ 0 in either basis) is broken, so the message surfaces BOTH the variance total and the
/// per-basis clearing residual — otherwise a clearing-only mismatch (variance 0, clearing ≠ 0) would
/// read as the confusing "non-zero variance of 0.00".
/// </para>
/// <para>
/// No row is written and no audit event is created when this is thrown — the gate fires before any
/// side effect, mirroring the <c>StatementNotBalancedException</c> precedent (M5/WP-04).
/// </para>
/// </summary>
public sealed class MigrationNotTiedException(
    Guid verificationId, decimal varianceTotal, decimal clearingCash, decimal clearingAccrual)
    : Exception(
        $"Verification {verificationId} no longer ties: variance total {varianceTotal:0.00}, " +
        $"clearing residual cash {clearingCash:0.00} / accrual {clearingAccrual:0.00}. " +
        "Sign-off is blocked until all variances are $0.00 and clearing nets to $0.00 in both bases.")
{
    public Guid VerificationId { get; } = verificationId;
    public decimal VarianceTotal { get; } = varianceTotal;
    public decimal ClearingCash { get; } = clearingCash;
    public decimal ClearingAccrual { get; } = clearingAccrual;
}
