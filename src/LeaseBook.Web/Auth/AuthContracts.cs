namespace LeaseBook.Web.Auth;

// Auth API contract (§C.6). WP-08 generates its TypeScript client from these via OpenAPI.

public sealed record LoginRequest(string Email, string Password);

/// <summary><c>Status</c> is "ok" (signed in) or "mfa-required" (then call <c>/mfa</c>).</summary>
public sealed record LoginResponse(string Status, string? MfaToken);

public sealed record MfaRequest(string MfaToken, string Code);

public sealed record ConfirmMfaRequest(string Code);

public sealed record MeResponse(Guid UserId, string? Name, string? Email, string? Role, Guid OrgId, string? OrgName, bool MfaEnabled = false, bool MfaEnrollmentRequired = false);

public sealed record EnrollResponse(string OtpauthUri, string Secret);

public static class LoginStatus
{
    public const string Ok = "ok";
    public const string MfaRequired = "mfa-required";
}

public sealed record RecoveryCodesResponse(IReadOnlyList<string> Codes);
public sealed record RecoveryLoginRequest(string MfaToken, string Code);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
