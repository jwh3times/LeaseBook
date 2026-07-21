namespace LeaseBook.Web.Onboarding.Verification;

/// <summary>
/// Thrown by <see cref="VerificationService.SignOffAsync"/> when the stored verification carries no
/// held-fees attestation yet the current journal carries a non-zero trust-side held-fees position
/// (WP-7 §4/D5). Such a row can read TIED — held fees never entered its variance — while fiduciary
/// held fees sit unverified, so sign-off must refuse. Mapped to HTTP 409
/// <c>held_fees_not_attested</c> at the sign-off endpoint.
/// <para>
/// Two paths reach this gate and the message must fit both: a pre-WP-7 row whose expectation lacks
/// the property entirely, and a current-day operator who imported held fees and then left the
/// wizard's "leave blank if none" field empty (a blank field sends null, which is never defaulted to
/// 0.00). Naming only the first would blame a stale row for what is usually a blank field.
/// </para>
/// <para>
/// No row is written and no audit event is created when this is thrown — the gate fires before any
/// side effect, mirroring the <see cref="MigrationNotTiedException"/> precedent. Recovery is one
/// re-verify (which captures a held-fees attestation).
/// </para>
/// </summary>
public sealed class HeldFeesNotAttestedException(Guid verificationId)
    : Exception("This verification has no held PM fees figure — either it was recorded before held PM fees were tracked, or the held PM fees total was left blank. Re-run verification with that figure to include them.")
{
    public Guid VerificationId { get; } = verificationId;
}
