using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// F8 / ADR-041: the Data Protection keyring outlives the process that created it.
/// <para>
/// This is the test the finding could not have before. The old configuration persisted keys to the
/// container filesystem, so the property below was only observable after a real container recreate —
/// which is why F8 sat open across two work packages as "validate in Track B". Persisting to Postgres
/// makes the same property checkable here: build a provider, protect something, throw the provider
/// away entirely, build a fresh one against the same database, and read it back.
/// </para>
/// <para>
/// What it guards is an availability failure, not a confidentiality one. If the keyring is lost,
/// every encrypted <c>asp_net_user_tokens.value</c> row becomes permanently undecryptable and every
/// MFA-enrolled account is locked out.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DataProtectionKeyringTests(PostgresFixture fixture)
{
    private const string Purpose = "LeaseBook.Tests.Keyring";

    /// <summary>
    /// Stands up Data Protection exactly as <c>Program.cs</c> does — same application name, same
    /// keyring context — in a service provider of its own, so disposing it is a faithful stand-in for
    /// losing the process.
    /// </summary>
    private ServiceProvider BuildProviderAsIfFreshContainer() =>
        new ServiceCollection()
            .AddLogging()
            .AddDbContext<KeyringDbContext>(options => options
                .UseNpgsql(fixture.MigratorConnectionString)
                .UseSnakeCaseNamingConvention())
            .AddDataProtection()
                .SetApplicationName("LeaseBook")
                .PersistKeysToDbContext<KeyringDbContext>()
                .Services
            .BuildServiceProvider();

    [Fact]
    public async Task Protected_payload_survives_a_completely_new_process()
    {
        const string secret = "JBSWY3DPEHPK3PXP"; // shaped like a TOTP secret; not a real one

        string protectedPayload;
        await using (var first = BuildProviderAsIfFreshContainer())
        {
            protectedPayload = first.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(Purpose)
                .Protect(secret);
        }

        // Nothing of the first provider survives — no keyring cache, no filesystem, no singletons.
        await using var second = BuildProviderAsIfFreshContainer();

        var roundTripped = second.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose)
            .Unprotect(protectedPayload);

        roundTripped.ShouldBe(
            secret,
            "the keyring must be readable by a process that did not create it. If this fails, a " +
            "container recreate has just locked every MFA-enrolled account out permanently.");
    }

    /// <summary>
    /// The regression guard on <c>Program.cs</c> itself. The two tests above prove the mechanism
    /// works; this one proves the running host actually uses it — a payload protected by the real
    /// application is readable by a provider that shares only the database. If someone drops
    /// <c>PersistKeysToDbContext</c>, or changes <c>SetApplicationName</c>, the host falls back to a
    /// keyring this provider cannot see and this fails.
    /// </summary>
    [Fact]
    public async Task The_running_host_protects_with_the_database_backed_keyring()
    {
        var protectedByHost = fixture.Api.Services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose)
            .Protect("written-by-the-real-host");

        await using var standalone = BuildProviderAsIfFreshContainer();

        var roundTripped = standalone.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose)
            .Unprotect(protectedByHost);

        roundTripped.ShouldBe(
            "written-by-the-real-host",
            "the host's keyring must be the shared database one. A failure here means Program.cs " +
            "silently went back to a process-local keyring — exactly the F8 regression.");
    }

    [Fact]
    public async Task Keyring_is_persisted_to_the_database_not_the_filesystem()
    {
        await using var provider = BuildProviderAsIfFreshContainer();

        // Protecting forces the keyring to materialize a key if the table is empty.
        provider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose)
            .Protect("force-key-creation");

        await using var scope = provider.CreateAsyncScope();
        var keys = await scope.ServiceProvider.GetRequiredService<KeyringDbContext>()
            .DataProtectionKeys.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

        keys.ShouldNotBeEmpty("the keyring must land in data_protection_keys, not on disk");
        keys.ShouldAllBe(k => k.Xml != null && k.Xml.Contains("<key", StringComparison.Ordinal));
    }
}
