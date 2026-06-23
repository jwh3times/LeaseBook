namespace LeaseBook.Web.Onboarding.Verification;

/// <summary>
/// Thrown by <see cref="VerificationService.SignOffAsync"/> when the referenced verification row
/// is not tied (<see cref="IsTied"/> == false). Mapped to HTTP 409 <c>not_tied</c> at the sign-off
/// endpoint.
/// <para>
/// No row is written and no audit event is created when this is thrown — the gate fires before any
/// side effect, mirroring the <c>StatementNotBalancedException</c> precedent (M5/WP-04).
/// </para>
/// </summary>
public sealed class MigrationNotTiedException(Guid verificationId, decimal varianceTotal)
    : Exception(
        $"Verification {verificationId} has a non-zero variance of {varianceTotal:0.00}; " +
        "sign-off is blocked until all variances are resolved and clearing nets to $0.00 in both bases.")
{
    public Guid VerificationId { get; } = verificationId;
    public decimal VarianceTotal { get; } = varianceTotal;
}
