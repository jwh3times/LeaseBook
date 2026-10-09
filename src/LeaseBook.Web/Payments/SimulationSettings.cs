using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Processing;

namespace LeaseBook.Web.Payments;

/// <summary>What the Stripe sandbox mode needs beyond the fixture bindings. Held in local secrets, never in a manifest.</summary>
public sealed class StripeSandboxSettings
{
    public string SecretKey { get; set; } = "";
    /// <summary>The secret Stripe signs this host's callbacks with (whsec_…). Nothing delivered is read without it.</summary>
    public string WebhookSecret { get; set; } = "";
}

public sealed partial class SimulationSettings
{
    // Every setting a manifest or an operator may supply, by its full path under Payments. Anything
    // else is refused wherever it sits, so nothing can ride in beside a fixture or under a known key.
    [GeneratedRegex(@"^(Mode|SigningKey|ManifestPath|Fixtures:\d+:(OrgId|Generation|BankId|Account))\z", RegexOptions.CultureInvariant)]
    private static partial Regex KnownSetting();

    // What a sandbox host may be given besides, and no other mode may.
    [GeneratedRegex(@"^(Stripe:(SecretKey|WebhookSecret)|Fixtures:\d+:(CardPaymentMethod|AchPaymentMethod))\z", RegexOptions.CultureInvariant)]
    private static partial Regex SandboxSetting();

    // Stripe's documented test payment methods, by their shape. A payer's saved method is pm_ and an
    // opaque id with no such prefix, so nothing admitted here can be a real one.
    [GeneratedRegex(@"^pm_card_[A-Za-z0-9_]{1,60}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TestCard();
    [GeneratedRegex(@"^pm_usBankAccount_[A-Za-z0-9_]{1,60}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TestBankAccount();

    public string Mode { get; set; } = PaymentModes.Disabled;
    public string SigningKey { get; set; } = "";
    public FixtureBinding[] Fixtures { get; set; } = [];
    [JsonIgnore]
    public StripeSandboxSettings? Stripe { get; set; }
    /// <summary>Payments are reachable for the bound fixture organizations, through whichever processor the mode names.</summary>
    [JsonIgnore]
    public bool Enabled => Mode is PaymentModes.Simulation or PaymentModes.StripeSandbox;
    /// <summary>The processor is the local simulator. Its signed notices are accepted in this mode only.</summary>
    [JsonIgnore]
    public bool Simulated => Mode == PaymentModes.Simulation;

    public FixtureBinding? ForOrg(Guid? orgId) => Enabled ? Fixtures.SingleOrDefault(x => x.OrgId == orgId) : null;
    public FixtureBinding? ForAccount(string account) => Enabled ? Fixtures.SingleOrDefault(x => x.Account == account) : null;

    /// <summary>
    /// Adds a fixture manifest as the last configuration source. A manifest names fixtures and their
    /// signing key. Provider credentials stay in local secrets, so a manifest that holds any is refused.
    /// </summary>
    public static void AddManifest(IConfigurationBuilder configuration, string path)
    {
        var full = Path.GetFullPath(path);
        if (new ConfigurationBuilder().AddJsonFile(full, optional: false, reloadOnChange: false).Build()
            .GetSection("Payments:Stripe").Exists())
        { throw new InvalidOperationException("A payment fixture manifest must not hold provider credentials."); }
        configuration.AddJsonFile(full, optional: false, reloadOnChange: false);
    }

    public static SimulationSettings Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("Payments");
        var settings = section.Get<SimulationSettings>() ?? new();
        var sandbox = settings.Mode == PaymentModes.StripeSandbox;
        // Provider configuration is refused wherever the mode does not call for it, payments disabled
        // included: a key that is present but unused is a key waiting for a mode nobody reviewed.
        // Named by its path only, never its value, so the refusal says what to remove without echoing a key.
        // A parent node is judged only when it holds a value of its own: some configuration sources
        // report every parent with an empty one.
        var supplied = section.AsEnumerable(makePathsRelative: true).Where(x => x.Value is not null).ToArray();
        var unknown = supplied
            .Where(x => x.Value != "" || !supplied.Any(other => other.Key.StartsWith(x.Key + ":", StringComparison.Ordinal)))
            .Select(x => x.Key)
            .FirstOrDefault(key => !KnownSetting().IsMatch(key) && !(sandbox && SandboxSetting().IsMatch(key)));
        if (settings.Mode is not (PaymentModes.Disabled or PaymentModes.Simulation or PaymentModes.StripeSandbox)
            || configuration.GetSection("Stripe").Exists() || unknown is not null)
        {
            throw new InvalidOperationException("This release supports Disabled, fixture-only Simulation or StripeSandbox payments; other provider configuration is refused."
                + (unknown is null ? "" : $" Unknown setting: Payments:{unknown}."));
        }
        // A fixture's account names its processor: a simulator account in a sandbox host, or the other
        // way round, is a manifest written for a different mode.
        var accountPrefix = sandbox ? "acct_" : "sim_";
        if (settings.Enabled && (!environment.IsDevelopment() || settings.SigningKey.Length < 32
            || settings.Fixtures.Length == 0 || settings.Fixtures.Any(x => x.OrgId == Guid.Empty
                || x.Generation == Guid.Empty || x.BankId == Guid.Empty
                || !x.Account.StartsWith(accountPrefix, StringComparison.Ordinal)
                || x.Account.Length <= accountPrefix.Length || x.Account.Length > 100)
            || settings.Fixtures.Select(x => x.OrgId).Distinct().Count() != settings.Fixtures.Length
            || settings.Fixtures.Select(x => x.Account).Distinct().Count() != settings.Fixtures.Length))
        { throw new InvalidOperationException("Simulation and StripeSandbox require Development, a fixture signing key and unique explicit fixture bindings to accounts of the mode's processor."); }
        // Only a test-mode key. A live key is refused by its prefix, before any client could be built from it.
        if (sandbox && !TestKey(settings.Stripe?.SecretKey))
        { throw new InvalidOperationException("StripeSandbox requires a Stripe test-mode secret key."); }
        // And the secret its callbacks are signed with: a host that could not tell Stripe's events from
        // anyone's must not serve the route. Named by its path and its prefix, never by what was given.
        if (sandbox && !SigningSecret(settings.Stripe?.WebhookSecret))
        { throw new InvalidOperationException("StripeSandbox requires a Stripe webhook signing secret (whsec_…) in Payments:Stripe:WebhookSecret."); }
        for (var i = 0; sandbox && i < settings.Fixtures.Length; i++)
        {
            var wrong = !TestCard().IsMatch(settings.Fixtures[i].CardPaymentMethod ?? "") ? "CardPaymentMethod"
                : !TestBankAccount().IsMatch(settings.Fixtures[i].AchPaymentMethod ?? "") ? "AchPaymentMethod" : null;
            if (wrong is not null)
            {
                throw new InvalidOperationException("StripeSandbox collects only with Stripe's documented test payment methods"
                    + $" (pm_card_… for a card, pm_usBankAccount_… for ACH). Not one: Payments:Fixtures:{i}:{wrong}.");
            }
        }
        // The mode is the host's, so a binding takes it from here and never from its own manifest entry.
        settings.Fixtures = [.. settings.Fixtures.Select(x => x with { Mode = settings.Mode })];
        return settings;
    }

    private static bool TestKey(string? key) => key is not null
        && new[] { "sk_test_", "rk_test_" }.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length);

    private static bool SigningSecret(string? secret) => secret is not null
        && secret.StartsWith("whsec_", StringComparison.Ordinal) && secret.Length > "whsec_".Length;

    /// <summary>Registers the one processor this host's mode uses.</summary>
    public void AddProcessor(IServiceCollection services)
    {
        // One or the other, never both: a sandbox host has no simulator to fall back on or to sign for.
        if (Mode == PaymentModes.StripeSandbox) { LeaseBook.Web.Payments.Stripe.StripeSandboxProcessor.Register(services); return; }
        services.AddSingleton<SimulatedProcessor>();
        services.AddSingleton<IPaymentProcessor>(sp => sp.GetRequiredService<SimulatedProcessor>());
    }

    public static async Task ValidateFixturesAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<SimulationSettings>();
        foreach (var binding in settings.Fixtures.Where(_ => settings.Enabled))
        {
            await using var scope = services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<SharedKernel.Tenancy.OrgScopedExecutor>().RunAsSystemAsync(binding.OrgId,
                "payments:startup", async () =>
                {
                    await sp.GetRequiredService<PaymentEngine>().RequireFixtureAsync(binding, ct);
                    var banks = await sp.GetRequiredService<SharedKernel.Cqrs.ISender>()
                        .Query(new Modules.Directory.Features.BankAccounts.ListBankAccounts(true), ct);
                    if (!banks.Any(x => x.Id == binding.BankId && x.Purpose == "trust"))
                    { throw new PaymentUnavailableException(); }
                }, ct);
        }
        // Whose key this is cannot be read from its text, so Stripe is asked, once, after everything
        // that can be checked locally. Only a host about to serve or to collect gets here.
        if (settings.Mode == PaymentModes.StripeSandbox)
        { await services.GetRequiredService<LeaseBook.Web.Payments.Stripe.StripeSandboxProcessor>().RequirePlatformKeyAsync(ct); }
    }
}
