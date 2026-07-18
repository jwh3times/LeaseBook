namespace LeaseBook.Web.Auth;

/// <summary>Auth policy toggles, bound from configuration section <c>Auth</c>.</summary>
public sealed class AuthOptions
{
    /// <summary>When true, PMAdmin principals must have completed TOTP enrollment to reach any
    /// non-exempt endpoint. Default false (Development/tests); true in appsettings.Production.json.</summary>
    public bool EnforceAdminMfa { get; set; }
}
