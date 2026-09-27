using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Processing;

namespace LeaseBook.Web.Payments;

public sealed class SimulationSettings
{
    public string Mode { get; set; } = "Disabled";
    public string SigningKey { get; set; } = "";
    public FixtureBinding[] Fixtures { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Enabled => Mode == "Simulation";

    public FixtureBinding? ForOrg(Guid? orgId) => Enabled ? Fixtures.SingleOrDefault(x => x.OrgId == orgId) : null;
    public FixtureBinding? ForAccount(string account) => Enabled ? Fixtures.SingleOrDefault(x => x.Account == account) : null;

    public static SimulationSettings Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("Payments");
        var settings = section.Get<SimulationSettings>() ?? new();
        if (settings.Mode is not ("Disabled" or "Simulation")
            || configuration.GetSection("Stripe").GetChildren().Any()
            || section.GetChildren().Any(x => x.Key is not ("Mode" or "SigningKey" or "Fixtures" or "ManifestPath")))
        { throw new InvalidOperationException("This release supports Disabled or fixture-only Simulation payments; provider configuration is refused."); }
        if (settings.Enabled && (!environment.IsDevelopment() || settings.SigningKey.Length < 32
            || settings.Fixtures.Length == 0 || settings.Fixtures.Any(x => x.OrgId == Guid.Empty
                || x.Generation == Guid.Empty || x.BankId == Guid.Empty || !x.Account.StartsWith("sim_", StringComparison.Ordinal)
                || x.Account.Length > 100)
            || settings.Fixtures.Select(x => x.OrgId).Distinct().Count() != settings.Fixtures.Length
            || settings.Fixtures.Select(x => x.Account).Distinct().Count() != settings.Fixtures.Length))
        { throw new InvalidOperationException("Simulation requires Development, a fixture signing key and unique explicit fixture bindings."); }
        return settings;
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
    }
}
