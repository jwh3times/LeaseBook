using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;

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

        RequireCertificateVerifyingTls(config, environment, "ConnectionStrings:Default");
        RequireCertificateVerifyingTls(config, environment, "ConnectionStrings:Migrations");
    }

    /// <summary>
    /// Refuses a Postgres connection string that does not verify the server's certificate.
    ///
    /// <para>
    /// Npgsql's default is <c>SSL Mode=Prefer</c>, which negotiates TLS and then accepts whatever
    /// certificate it is handed. That encrypts the traffic and authenticates nothing, so an attacker
    /// positioned on the path can present any certificate and read — or alter — everything, including
    /// the credential used to open the connection. "TLS is on" and "we know who we are talking to"
    /// are different properties, and only the first one came for free.
    /// </para>
    ///
    /// <para>
    /// The case that decides this is not the application. Production traffic stays inside the VNet,
    /// but <b>migrations run from a hosted CI runner across the public internet carrying the
    /// schema-owner credential</b> — the one connection with authority to rewrite the schema, over
    /// the one path nobody controls. Both strings are checked because the weaker of the two is the
    /// one that matters.
    /// </para>
    ///
    /// <para>
    /// A key that is absent or empty is not checked: it is not a weak setting, it is no setting, and
    /// it surfaces immediately as a connection failure rather than as silent exposure. Only a string
    /// that is present and too permissive is a misconfiguration that would otherwise work perfectly
    /// and look fine.
    /// </para>
    /// </summary>
    private static void RequireCertificateVerifyingTls(
        IConfiguration config, IWebHostEnvironment environment, string key)
    {
        var connectionString = config[key];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        SslMode sslMode;
        try
        {
            sslMode = new NpgsqlConnectionStringBuilder(connectionString).SslMode;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                $"'{key}' is not a valid Npgsql connection string, so its TLS mode cannot be " +
                $"verified before the '{environment.EnvironmentName}' environment starts serving.",
                ex);
        }

        // Named explicitly rather than compared by ordinal: the enum's numeric order is Npgsql's to
        // change, and a '>=' that silently starts admitting a weaker mode after a package bump is
        // exactly the kind of regression this guard exists to prevent.
        if (sslMode is SslMode.VerifyCA or SslMode.VerifyFull)
        {
            return;
        }

        throw new InvalidOperationException(
            $"'{key}' has SSL Mode={sslMode} in the '{environment.EnvironmentName}' environment, " +
            "which does not verify the server's certificate — the connection is encrypted but the " +
            "server is not authenticated, so anyone on the network path can impersonate it and read " +
            "the credential. Set 'SSL Mode=VerifyFull' in the connection string. Azure Database for " +
            "PostgreSQL Flexible Server presents certificates that chain to public CAs, so no " +
            "'Root Certificate' parameter is needed.");
    }
}
