namespace LeaseBook.Web.Security;

/// <summary>Auth-endpoint rate-limit knobs, bound from config section "RateLimiting". Generous in
/// Development (localhost needs no throttling); strict in Production via appsettings.Production.json.</summary>
public sealed class RateLimitingOptions
{
    public int AuthPermitLimit { get; set; } = 20;
    public int AuthWindowSeconds { get; set; } = 60;
}
