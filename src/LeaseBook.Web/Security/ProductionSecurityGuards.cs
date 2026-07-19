using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace LeaseBook.Web.Security;

/// <summary>Fail-fast startup validation for configuration that is safe to leave permissive in
/// Development but must never boot insecure outside it. A no-op in Development so the whole
/// test/e2e suite is unaffected; throws <see cref="InvalidOperationException"/> in every other
/// environment so a misconfigured deploy never silently serves traffic.</summary>
public static class ProductionSecurityGuards
{
    public static void Validate(IConfiguration config, IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        var allowedHosts = config["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts == "*")
        {
            throw new InvalidOperationException(
                $"Host filtering is disabled in the '{environment.EnvironmentName}' environment " +
                $"(AllowedHosts is {(string.IsNullOrWhiteSpace(allowedHosts) ? "empty" : "'*'")}). " +
                "Set the 'AllowedHosts' configuration key to the real hostname(s) " +
                "(semicolon-separated, e.g. 'app.leasebook.com;www.leasebook.com') before starting " +
                "this environment.");
        }

        // F8: the Identity token store (TOTP secret + recovery codes) is encrypted at rest with
        // ASP.NET Data Protection. The default keyring persists to local, per-instance disk — outside
        // Development that keyring must be durable/shared (e.g. persisted to Key Vault) or a restart /
        // scale-out loses the keys, permanently locking out every MFA-enrolled account with no
        // break-glass path. This flag is the operator's attestation that a durable keyring is wired.
        if (!config.GetValue<bool>("DataProtection:Durable"))
        {
            throw new InvalidOperationException(
                $"A durable Data Protection keyring is required in the '{environment.EnvironmentName}' " +
                "environment (finding F8). The Identity token store (TOTP secret and recovery codes) is " +
                "encrypted at rest with ASP.NET Data Protection; the default keyring is ephemeral / " +
                "per-instance and is lost on restart or scale-out, which permanently locks out every " +
                "MFA-enrolled account with no break-glass path. Configure a durable, shared keyring " +
                "(persisted to Key Vault or an equivalent shared store) and then set the " +
                "'DataProtection:Durable' configuration key to true.");
        }
    }
}
