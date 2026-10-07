using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Payments;

public sealed class SimulatedProcessor(IServiceScopeFactory scopes, SimulationSettings settings, TimeProvider clock) : IPaymentProcessor
{
    public Task<ProcessorResult> SubmitAsync(ProcessorRequest request, CancellationToken ct) => AccessAsync(request, true, ct);
    public Task<ProcessorResult> LookupAsync(ProcessorRequest request, CancellationToken ct) => AccessAsync(request, false, ct);

    private async Task<ProcessorResult> AccessAsync(ProcessorRequest request, bool create, CancellationToken ct)
    {
        if (settings.ForOrg(request.Binding.OrgId) != request.Binding) { throw new PaymentUnavailableException(); }
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        return await sp.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(request.Binding.OrgId, "payments:provider", async () =>
        {
            var engine = sp.GetRequiredService<PaymentEngine>();
            await engine.RequireFixtureAsync(request.Binding, ct);
            await engine.LockAsync("provider:" + request.OperationId, ct);
            var row = await db.Set<SimulatedCollection>().SingleOrDefaultAsync(x => x.Id == request.OperationId, ct);
            if (row is not null)
            {
                if (row.Fingerprint != request.Fingerprint || row.Generation != request.Binding.Generation)
                { throw new PaymentConflictException(); }
                return new ProcessorResult("Accepted", row.ProviderId);
            }
            if (!create) { return new ProcessorResult("Absent", null); }
            row = new SimulatedCollection
            {
                Id = request.OperationId,
                Generation = request.Binding.Generation,
                Fingerprint = request.Fingerprint,
                ProviderId = $"sim_{request.Binding.Generation:N}_{request.OperationId:N}",
            };
            db.Add(row); await db.SaveChangesAsync(ct);
            return new ProcessorResult("Accepted", row.ProviderId);
        }, ct);
    }

    // Authentic, recent, and small enough to have been read whole. Nothing is parsed before this passes.
    private bool Verified(byte[] rawBody, string signature)
    {
        if (!settings.Enabled || rawBody.Length > 16384) { return false; }
        var parts = signature.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0 || seconds > 253402300799
            || Math.Abs((clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds)).TotalSeconds) > 300)
        { return false; }
        byte[] supplied;
        try { supplied = Convert.FromHexString(parts[1]); } catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(supplied, Signature(rawBody, parts[0]));
    }

    public ProcessorNotice? Authenticate(byte[] rawBody, string signature) =>
        Verified(rawBody, signature) ? new ProcessorNotice(rawBody) : null;

    // The simulator's notices carry everything, so reading one fetches nothing. Whether a notice is
    // for a fixture here is the runner's to decide, from the account, mode and generation it names.
    public Task<ProcessorRead<ProcessorSettlement>> ReadSettlementAsync(ProcessorNotice notice, CancellationToken ct) =>
        Task.FromResult(ProcessorRead<ProcessorSettlement>.Of(Settlement(notice.Body)));

    public Task<ProcessorRead<ProcessorObservation>> ReadObservationAsync(ProcessorNotice notice, CancellationToken ct) =>
        Task.FromResult(ProcessorRead<ProcessorObservation>.Of(Observation(notice.Body)));

    private static ProcessorSettlement? Settlement(byte[] rawBody)
    {
        try
        {
            // Only what must hold to route and to do arithmetic on the evidence. Whether it can be
            // stored, and what it says, is the engine's to judge and to record.
            var value = JsonSerializer.Deserialize<ProcessorSettlement>(rawBody);
            if (value is null || value.Account is null || value.Mode is null || value.Items is null
                || value.Items.Any(x => x is null) || value.ObservedAt.Kind != DateTimeKind.Utc
                || value.Items.SelectMany(x => new[] { x.Gross, x.Fee, x.Net }).Append(value.BankAmount)
                    .Any(x => Math.Abs(x) > 999999999999.99m || decimal.Round(x, 2) != x))
            { return null; }
            return value;
        }
        catch (JsonException) { return null; }
    }

    private static ProcessorObservation? Observation(byte[] rawBody)
    {
        try
        {
            var value = JsonSerializer.Deserialize<ProcessorObservation>(rawBody);
            if (value is null || new[] { value.EventId, value.ProviderId, value.Account, value.EvidenceId, value.PayoutId }
                .Any(x => x is null || x.Length > 100) || string.IsNullOrEmpty(value.EventId)
                || string.IsNullOrEmpty(value.ProviderId) || value.Currency is null || value.Currency.Length != 3
                || value.Mode is null || value.Mode.Length > 20 || value.Kind is null || value.Kind.Length > 40
                || value.ObservedAt.Kind != DateTimeKind.Utc
                || new[] { value.Gross, value.Fee, value.Net }.Any(x => Math.Abs(x) > 999999999999.99m || decimal.Round(x, 2) != x))
            { return null; }
            return value;
        }
        catch (JsonException) { return null; }
    }

    // Only CLI/tests can call this. There is no HTTP success override or signing key in the SPA.
    public string Sign(byte[] body)
    {
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return timestamp + "." + Convert.ToHexStringLower(Signature(body, timestamp));
    }

    private byte[] Signature(byte[] body, string timestamp) => HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(settings.SigningKey), (byte[])[.. Encoding.UTF8.GetBytes(timestamp + "."), .. body]);
}
