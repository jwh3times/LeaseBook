using System.Globalization;
using System.Text.Json;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Cli;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Payments;

internal sealed class PaymentSimulationVerb : ICliVerb
{
    public string Name => "payment-simulation";
    public bool TryCreateInvocation(string[] args, out CliInvocation invocation, out string error)
    {
        invocation = null!;
        error = "payment-simulation: init <manifest.local> | step | emit <org-id> <operation-id> <kind> <bank-date yyyy-MM-dd>";
        if (args.Length == 3 && args[1] == "init" && args[2].EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            invocation = new(Name, async (sp, ct) =>
            { await PaymentFixtureBootstrap.CreateManifestAsync(sp, args[2], ct); return 0; });
        }
        else if (args.Length == 2 && args[1] == "step")
        {
            invocation = new(Name, async (sp, ct) => { await sp.GetRequiredService<PaymentRunner>().RunOnceAsync(ct); return 0; });
        }
        else if (args.Length == 6 && args[1] == "emit" && Guid.TryParse(args[2], out var orgId)
            && Guid.TryParse(args[3], out var operationId) && PaymentEngine.EventKinds.Contains(args[4])
            && DateOnly.TryParseExact(args[5], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            invocation = new(Name, (sp, ct) => Emit(sp, orgId, operationId, args[4], date, ct));
        }
        else { return false; }
        error = ""; return true;
    }

    private static async Task<int> Emit(IServiceProvider services, Guid orgId, Guid operationId, string kind, DateOnly date, CancellationToken ct)
    {
        var settings = services.GetRequiredService<SimulationSettings>();
        var binding = settings.ForOrg(orgId) ?? throw new PaymentUnavailableException();
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var op = await sp.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(orgId, "payments:fixture-driver", async () =>
        {
            await sp.GetRequiredService<PaymentEngine>().RequireFixtureAsync(binding, ct);
            return await sp.GetRequiredService<AppDbContext>().Set<PaymentOperation>().AsNoTracking().SingleAsync(x => x.Id == operationId, ct);
        }, ct);
        var processor = services.GetRequiredService<SimulatedProcessor>();
        var request = new ProcessorRequest(binding, operationId, op.Fingerprint);
        var provider = await processor.LookupAsync(request, ct);
        if (provider.Outcome == "Absent") { provider = await processor.SubmitAsync(request, ct); }
        // Bank credit evidence is the clean item: the charge, the quoted fee, and the ledger amount net.
        // Every other kind keeps the fee-free shape; returns of a fee-bearing payment come with batches.
        var credit = kind == "BankCredit";
        var observation = new ProcessorObservation($"{operationId:N}:{kind}:{date:yyyyMMdd}", provider.ProviderId!, binding.Account,
            "Simulation", binding.Generation, kind, credit ? op.ChargedAmount : op.Amount, credit ? op.QuotedFee : 0,
            op.Amount, "USD", binding.BankId, date,
            "bank-" + operationId.ToString("N"), "payout-" + operationId.ToString("N"), true,
            date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var body = JsonSerializer.SerializeToUtf8Bytes(observation);
        var verified = processor.VerifyAndNormalize(body, processor.Sign(body)) ?? throw new InvalidOperationException("Fixture signature failed.");
        await services.GetRequiredService<PaymentRunner>().ReceiveAsync(verified, ct);
        await services.GetRequiredService<PaymentRunner>().RunOnceAsync(ct);
        return 0;
    }
}
