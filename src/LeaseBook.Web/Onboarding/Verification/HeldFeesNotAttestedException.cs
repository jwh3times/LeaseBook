namespace LeaseBook.Web.Onboarding.Verification;

/// <summary>
/// Thrown by <see cref="VerificationService.SignOffAsync"/> when the stored verification predates
/// held-fees tracking (its expectation carries no held-fees attestation) yet the current journal
/// carries a non-zero trust-side held-fees position (WP-7 §4/D5). Such a row can read TIED — held
/// fees never entered its variance — while fiduciary held fees sit unverified, so sign-off must
/// refuse. Mapped to HTTP 409 <c>held_fees_not_attested</c> at the sign-off endpoint.
/// <para>
/// No row is written and no audit event is created when this is thrown — the gate fires before any
/// side effect, mirroring the <see cref="MigrationNotTiedException"/> precedent. Recovery is one
/// re-verify (which captures a held-fees attestation).
/// </para>
/// </summary>
public sealed class HeldFeesNotAttestedException(Guid verificationId)
    : Exception("This verification was recorded before held PM fees were tracked. Re-run verification to include them.")
{
    public Guid VerificationId { get; } = verificationId;
}
