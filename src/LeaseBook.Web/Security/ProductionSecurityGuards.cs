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

        // Outside Development the app encrypts sensitive data at rest with ASP.NET Data Protection,
        // which requires a durable, shared keyring (e.g. persisted to Key Vault); the default keyring
        // is per-instance and unsuitable outside Development. This flag is the operator's attestation
        // that a durable keyring is configured.
        if (!config.GetValue<bool>("DataProtection:Durable"))
        {
            throw new InvalidOperationException(
                $"A durable Data Protection keyring is required in the '{environment.EnvironmentName}' " +
                "environment. Configure a durable, shared keyring (e.g. persisted to Key Vault) and set " +
                "the 'DataProtection:Durable' configuration key to true before starting this environment.");
        }
    }
}
