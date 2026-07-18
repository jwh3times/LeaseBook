namespace LeaseBook.Web.Auth;

/// <summary>Custom claim types minted into the auth cookie.</summary>
public static class AuthClaims
{
    /// <summary>"true"/"false" — whether the principal has completed TOTP enrollment. Read by the
    /// MFA-enforcement authorization handler; refreshed when enrollment is confirmed.</summary>
    public const string MfaEnrolled = "mfa_enrolled";
}
