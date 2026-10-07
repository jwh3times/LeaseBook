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
        error = "payment-simulation: init <manifest.local> | step | emit <org-id> <operation-id> <kind> <bank-date yyyy-MM-dd>"
            + " | payout <org-id> <bank-date yyyy-MM-dd> <payout-id> <line>... [--bank-amount=<amount>] [--type=<type>]"
            + " where <line> is pay:<operation-id>[:<processor-fee>], return:<operation-id>[:<fee-given-back>],"
            + " refund:<operation-id> or fee:<amount>";
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
        else if (args.Length >= 6 && args[1] == "payout" && Guid.TryParse(args[2], out var payoutOrgId)
            && DateOnly.TryParseExact(args[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bankDate)
            && PayoutRequest.TryParse(args[4], args[5..], out var payout))
        {
            invocation = new(Name, (sp, ct) => Payout(sp, payoutOrgId, bankDate, payout, ct));
        }
        else { return false; }
        error = ""; return true;
    }

    // Delivers payout evidence the way a processor would: signed, verified, then stored and checked.
    private static async Task<int> Payout(IServiceProvider services, Guid orgId, DateOnly bankDate, PayoutRequest request, CancellationToken ct)
    {
        var settings = services.GetRequiredService<SimulationSettings>();
        var binding = settings.ForOrg(orgId) ?? throw new PaymentUnavailableException();
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        var ids = request.Lines.Where(x => x.OperationId is not null).Select(x => x.OperationId!.Value).Distinct().ToArray();
        var operations = await executor.RunAsSystemAsync(orgId, "payments:fixture-driver", async () =>
        {
            await sp.GetRequiredService<PaymentEngine>().RequireFixtureAsync(binding, ct);
            return await sp.GetRequiredService<AppDbContext>().Set<PaymentOperation>().AsNoTracking()
                .Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        }, ct);
        if (ids.FirstOrDefault(id => !operations.ContainsKey(id)) is var missing && missing != Guid.Empty)
        {
            Console.Error.WriteLine($"payment-simulation: no payment {missing} in this fixture.");
            return 1;
        }
        var processor = services.GetRequiredService<SimulatedProcessor>();
        var providerIds = new Dictionary<Guid, string>();
        foreach (var operation in operations.Values)
        {
            var submitted = ProcessorRequest.For(binding, operation);
            var provider = await processor.LookupAsync(submitted, ct);
            if (provider.Outcome == "Absent") { provider = await processor.SubmitAsync(submitted, ct); }
            providerIds[operation.Id] = provider.ProviderId!;
        }
        var evidence = request.ToEvidence(binding, bankDate, operations, providerIds);
        var body = JsonSerializer.SerializeToUtf8Bytes(evidence);
        var verified = processor.Authenticate(body, processor.Sign(body)) is { } notice
            ? (await processor.ReadSettlementAsync(notice, ct)).Value : null;
        if (verified is null) { throw new InvalidOperationException("Fixture signature failed."); }
        // The worker first, so that every payment named has been dispatched and carries its reference.
        var runner = services.GetRequiredService<PaymentRunner>();
        await runner.RunOnceAsync(ct);
        var delivery = await runner.ReceiveSettlementAsync(verified, ct);
        if (delivery.Id is not { } id)
        {
            Console.Error.WriteLine("payment-simulation: the payout evidence was not kept.");
            return 1;
        }
        var outcome = await executor.RunAsSystemAsync(orgId, "payments:fixture-driver", () =>
            sp.GetRequiredService<AppDbContext>().Set<PaymentSettlement>().AsNoTracking().SingleAsync(x => x.Id == id, ct), ct);
        Console.WriteLine($"Payout {evidence.PayoutId}: bank amount {evidence.BankAmount.ToString("0.00", CultureInfo.InvariantCulture)}, "
            + $"{outcome.Status}{(outcome.Reason is null ? "" : $" ({outcome.Reason})")}.");
        return 0;
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
        var request = ProcessorRequest.For(binding, op);
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
        var verified = (processor.Authenticate(body, processor.Sign(body)) is { } notice
            ? (await processor.ReadObservationAsync(notice, ct)).Value : null) ?? throw new InvalidOperationException("Fixture signature failed.");
        await services.GetRequiredService<PaymentRunner>().ReceiveAsync(verified, ct);
        await services.GetRequiredService<PaymentRunner>().RunOnceAsync(ct);
        return 0;
    }
}

/// <summary>One line of a payout as the fixture CLI names it: a payment, a return, a refund or a standalone fee.</summary>
internal sealed record PayoutLineRequest(string Kind, Guid? OperationId, decimal? Amount);

/// <summary>
/// What the fixture operator asked a payout to contain. A line with no amount is the clean case: the
/// processor kept, or gave back, exactly the quoted fee. The bank amount is the sum of the lines unless
/// it is overridden, which is how an untied payout is produced.
/// </summary>
internal sealed record PayoutRequest(string PayoutId, IReadOnlyList<PayoutLineRequest> Lines, decimal? BankAmount, string PayoutType)
{
    public static bool TryParse(string payoutId, string[] rest, out PayoutRequest request)
    {
        request = null!;
        var lines = new List<PayoutLineRequest>();
        decimal? bankAmount = null;
        var payoutType = SettlementPayoutTypes.Standard;
        foreach (var arg in rest)
        {
            if (arg.StartsWith("--bank-amount=", StringComparison.Ordinal))
            {
                if (!TryAmount(arg["--bank-amount=".Length..], allowNegative: true, out var amount)) { return false; }
                bankAmount = amount;
                continue;
            }
            if (arg.StartsWith("--type=", StringComparison.Ordinal)) { payoutType = arg["--type=".Length..]; continue; }
            var parts = arg.Split(':');
            switch (parts[0])
            {
                case "fee" when parts.Length == 2 && TryAmount(parts[1], allowNegative: false, out var fee) && fee > 0m:
                    lines.Add(new(SettlementItemKinds.Fee, null, fee));
                    break;
                case "pay" or "return" or "refund" when parts.Length is 2 or 3 && Guid.TryParse(parts[1], out var operationId):
                    decimal? given = null;
                    if (parts.Length == 3)
                    {
                        if (parts[0] == "refund" || !TryAmount(parts[2], allowNegative: false, out var amount)) { return false; }
                        given = amount;
                    }
                    lines.Add(new(parts[0] switch { "pay" => SettlementItemKinds.Payment, "return" => SettlementItemKinds.Return, _ => "Refund" },
                        operationId, given));
                    break;
                default: return false;
            }
        }
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(payoutId) || payoutId.StartsWith("--", StringComparison.Ordinal)) { return false; }
        request = new(payoutId, lines, bankAmount, payoutType);
        return true;
    }

    public ProcessorSettlement ToEvidence(FixtureBinding binding, DateOnly bankDate,
        IReadOnlyDictionary<Guid, PaymentOperation> operations, IReadOnlyDictionary<Guid, string> providerIds)
    {
        var items = Lines.Select((line, index) =>
        {
            var item = (index + 1).ToString(CultureInfo.InvariantCulture);
            if (line.OperationId is not { } id) { return new ProcessorSettlementItem(item, line.Kind, "", 0m, line.Amount!.Value, -line.Amount.Value, "USD"); }
            var operation = operations[id];
            // The fee on a payment is what the processor kept; on a return, what it gives back. A
            // refund is shown as the whole charge going back, which is a line LeaseBook does not post.
            var fee = line.Kind == "Refund" ? 0m : line.Amount ?? operation.QuotedFee;
            var net = operation.ChargedAmount - fee;
            return new ProcessorSettlementItem(item, line.Kind, providerIds[id], operation.ChargedAmount, fee,
                line.Kind == SettlementItemKinds.Payment ? net : -net, "USD");
        }).ToArray();
        return new(PayoutId, binding.Account, "Simulation", binding.Generation, PayoutType,
            BankAmount ?? items.Sum(x => x.Net), "USD", binding.BankId, bankDate, "bank-" + PayoutId,
            bankDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), items);
    }

    private static bool TryAmount(string text, bool allowNegative, out decimal amount) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint | (allowNegative ? NumberStyles.AllowLeadingSign : NumberStyles.None),
            CultureInfo.InvariantCulture, out amount) && decimal.Round(amount, 2) == amount && (allowNegative || amount >= 0m);
}
