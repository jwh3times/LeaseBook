using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using Stripe;

namespace LeaseBook.Web.Payments.Stripe;

/// <summary>
/// The way out to Stripe. A host uses the default; a test supplies a handler that replays recorded
/// payloads, so that nothing in the suite reaches the network.
/// </summary>
public sealed class StripeTransport(HttpMessageHandler handler)
{
    public HttpMessageHandler Handler => handler;
}

/// <summary>
/// Collects a payment as a direct charge on a fixture's connected account in a Stripe sandbox, and
/// finds it again (ADR-054). It sends the amount, a test payment method and two ids of LeaseBook's
/// own; nothing that identifies a tenant. Callbacks and payouts are not read yet.
/// </summary>
public sealed class StripeSandboxProcessor : IPaymentProcessor
{
    private const string OperationKey = "leasebook_operation";
    private const string GenerationKey = "leasebook_generation";
    // Stripe may forget an idempotency key a day after it first saw it. Past this age a repeat could
    // be taken as a new charge, so nothing is sent and a person looks instead.
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(23);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int PageSize = 100;
    private const int MaxPages = 100;

    private readonly SimulationSettings _settings;
    private readonly TimeProvider _clock;
    private readonly StripeClient _client;

    public StripeSandboxProcessor(SimulationSettings settings, TimeProvider clock, StripeTransport transport)
    {
        if (settings.Mode != PaymentModes.StripeSandbox || settings.Stripe is null)
        { throw new InvalidOperationException("The Stripe sandbox processor runs only in a StripeSandbox host."); }
        _settings = settings;
        _clock = clock;
        // No retries inside the library: a repeat is the worker's to make, on its own schedule. The
        // timeout bounds one HTTP call, not an attempt: an attempt is a lookup, which can page, and
        // then a submit, so it can outlive the worker's lease. That costs an attempt and cannot charge
        // twice, because a repeat carries the same idempotency key and only the claim that still
        // holds the lease completes.
        var http = new HttpClient(transport.Handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        _client = new StripeClient(settings.Stripe.SecretKey,
            httpClient: new SystemNetHttpClient(http, maxNetworkRetries: 0, enableTelemetry: false));
    }

    /// <summary>Registers this adapter as the host's one processor.</summary>
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(new StripeTransport(new SocketsHttpHandler { AllowAutoRedirect = false }));
        services.AddSingleton<StripeSandboxProcessor>();
        services.AddSingleton<IPaymentProcessor>(sp => sp.GetRequiredService<StripeSandboxProcessor>());
    }

    public async Task<ProcessorResult> SubmitAsync(ProcessorRequest request, CancellationToken ct)
    {
        RequireBinding(request);
        var amount = MinorUnits(request);
        var ach = request.Method == PaymentMethods.Ach;
        if (!ach && request.Method != PaymentMethods.Card) { throw new PaymentConflictException(); }
        if (_clock.GetUtcNow().UtcDateTime - Created(request) > KeyLifetime) { throw new PaymentConflictException(); }
        var options = new PaymentIntentCreateOptions
        {
            Amount = amount,
            Currency = "usd",
            PaymentMethod = ach ? request.Binding.AchPaymentMethod : request.Binding.CardPaymentMethod,
            Confirm = true,
            // What the account accepts is its own setting; naming the types here is refused. No
            // redirect can be followed, because there is no payer in front of a server-side charge.
            AutomaticPaymentMethods = new() { Enabled = true, AllowRedirects = "never" },
            Metadata = new()
            {
                [OperationKey] = request.OperationId.ToString("D"),
                [GenerationKey] = request.Binding.Generation.ToString("D"),
            },
        };
        // Set only when there is one: the library sends a member that was assigned nothing as an empty field.
        // Nobody is present to accept a mandate online; a real one belongs with a payment form.
        if (ach) { options.MandateData = new() { CustomerAcceptance = new() { Type = "offline" } }; }
        PaymentIntent intent;
        try
        {
            intent = await _client.V1.PaymentIntents.CreateAsync(options, new RequestOptions
            {
                StripeAccount = request.Binding.Account,
                IdempotencyKey = request.OperationId.ToString("D"),
            }, ct);
        }
        // A refused charge still made a payment: Stripe answers with an error that carries it. The
        // collection exists, and that it failed arrives as an event like any other outcome.
        catch (StripeException ex) when (ex.StripeError?.PaymentIntent is { } refused) { intent = refused; }
        catch (StripeException ex) when (Refusal(ex) is { } refusal) { throw refusal; }
        return Accepted(intent);
    }

    public async Task<ProcessorResult> LookupAsync(ProcessorRequest request, CancellationToken ct)
    {
        RequireBinding(request);
        var amount = MinorUnits(request);
        var options = new PaymentIntentListOptions
        {
            Created = new DateRangeOptions { GreaterThanOrEqual = Created(request) - ClockSkew },
            Limit = PageSize,
        };
        var account = new RequestOptions { StripeAccount = request.Binding.Account };
        var found = new List<PaymentIntent>();
        for (var page = 0; ; page++)
        {
            // Not an answer either way: stopping here as "absent" would let the charge be made twice.
            if (page == MaxPages) { throw new IOException("The Stripe sandbox listed more payments than a lookup reads."); }
            // A read charges nothing, so no refusal of one says anything about the payment: whatever
            // Stripe answers but a list is left to be retried, and after that for staff to retry.
            var list = await _client.V1.PaymentIntents.ListAsync(options, account, ct);
            if (list.Data.Any(x => x.Livemode)) { throw new PaymentUnavailableException(); }
            found.AddRange(list.Data.Where(x => x.Metadata is not null && x.Metadata.TryGetValue(OperationKey, out var operation)
                && Guid.TryParse(operation, out var id) && id == request.OperationId));
            if (!list.HasMore || list.Data.Count == 0) { break; }
            options.StartingAfter = list.Data[^1].Id;
        }
        if (found.Count == 0) { return new ProcessorResult("Absent", null); }
        // One payment, and the one that was asked for. Anything else under this operation's id is
        // evidence that disagrees with what LeaseBook holds, which a person has to read.
        if (found.Count > 1 || found[0].Amount != amount || !string.Equals(found[0].Currency, "usd", StringComparison.Ordinal)
            || !found[0].Metadata.TryGetValue(GenerationKey, out var generation)
            || !Guid.TryParse(generation, out var parsed) || parsed != request.Binding.Generation)
        { throw new PaymentConflictException(); }
        return Accepted(found[0]);
    }

    // Step 5 reads Stripe's events. Until then nothing delivered to a sandbox host is authentic.
    public ProcessorNotice? Authenticate(byte[] rawBody, string signature) => null;

    public Task<ProcessorRead<ProcessorObservation>> ReadObservationAsync(ProcessorNotice notice, CancellationToken ct) =>
        Task.FromResult(ProcessorRead<ProcessorObservation>.NotOurs);

    public Task<ProcessorRead<ProcessorSettlement>> ReadSettlementAsync(ProcessorNotice notice, CancellationToken ct) =>
        Task.FromResult(ProcessorRead<ProcessorSettlement>.NotOurs);

    /// <summary>
    /// Refuses a key that is not the platform's. A connected account with its own dashboard has test
    /// keys of its own, which pass the prefix check and can read that account. So the key is asked
    /// whose it is, and each fixture's account is read with it: a test account that pays its own
    /// Stripe fees, or the host does not start. A refusal names a fixture's organization, never the key.
    /// </summary>
    public async Task RequirePlatformKeyAsync(CancellationToken ct)
    {
        // Stripe's own text for a refused key repeats part of it, so no refusal here carries Stripe's.
        var own = await ReadAsync(() => _client.V1.Accounts.GetSelfAsync(null, ct), ct)
            ?? throw new InvalidOperationException("StripeSandbox could not read the account of its Stripe key.");
        if (Live(own) || _settings.Fixtures.Any(x => x.Account == own.Id))
        { throw new InvalidOperationException("StripeSandbox requires the platform's test-mode key, not a connected account's own."); }
        foreach (var fixture in _settings.Fixtures)
        {
            var account = await ReadAsync(() => _client.V1.Accounts.GetAsync(fixture.Account, null, null, ct), ct);
            if (account is null || account.Id != fixture.Account || Live(account) || account.Controller?.Fees?.Payer != "account")
            {
                throw new InvalidOperationException(
                    $"StripeSandbox fixture organization {fixture.OrgId} is not bound to a test connected account of this platform that pays its own Stripe fees.");
            }
        }
    }

    private static async Task<Account?> ReadAsync(Func<Task<Account>> read, CancellationToken ct)
    {
        try { return await read(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    // Not the live barrier. That is the key's prefix, checked before a client exists: a test key
    // reads only test accounts, and an account does not say which mode it is in. This reads a member
    // Stripe does not send today, so that it is believed should Stripe ever send it.
    private static bool Live(Account account) => account.RawJsonElement is { ValueKind: System.Text.Json.JsonValueKind.Object } raw
        && raw.TryGetProperty("livemode", out var live) && live.ValueKind == System.Text.Json.JsonValueKind.True;

    private void RequireBinding(ProcessorRequest request)
    {
        if (_settings.ForOrg(request.Binding.OrgId) != request.Binding) { throw new PaymentUnavailableException(); }
    }

    // Stripe counts cents. Converted here and nowhere else, and only when nothing is lost by it.
    private static long MinorUnits(ProcessorRequest request)
    {
        var minor = request.Amount * 100m;
        if (request.Currency != "USD" || minor <= 0m || minor != decimal.Truncate(minor) || minor > long.MaxValue)
        { throw new PaymentConflictException(); }
        return (long)minor;
    }

    // The time is UTC wherever it came from, read the same way by the age check and by the search.
    private static DateTime Created(ProcessorRequest request) => DateTime.SpecifyKind(request.CreatedAt, DateTimeKind.Utc);

    // A live object is never this host's, and its id is not kept. Accepted is for a collection that
    // can still end without anyone here: collected, on its way, or refused, which arrives as an
    // event. Waiting on a payer, a confirmation or a capture, cancelled, or in a state nobody has
    // named, it would wait for ever, so a person reads it.
    private static ProcessorResult Accepted(PaymentIntent intent)
    {
        if (intent.Livemode || string.IsNullOrEmpty(intent.Id)) { throw new PaymentUnavailableException(); }
        if (intent.Status is not ("succeeded" or "processing" or "requires_payment_method")) { throw new PaymentConflictException(); }
        return new ProcessorResult("Accepted", intent.Id);
    }

    // What a refusal to charge means for the payment, or null when the same request may yet be
    // accepted: a connection that failed, a rate limit, a key still in use, a fault on Stripe's
    // side, or a key Stripe would not take. That last one charged nothing, and mending the key makes
    // the request valid, so it must stay retryable and not be parked where it can only be closed.
    private static Exception? Refusal(StripeException ex)
    {
        var status = (int)ex.HttpStatusCode;
        if (ex.StripeError is not { } error || status is 401 or 403 or 409 or 429 or >= 500) { return null; }
        return error.Type switch
        {
            // The same key with other parameters: this operation was already submitted as something else.
            "idempotency_error" => new PaymentConflictException(),
            // Stripe will not take this request however often it is sent.
            "invalid_request_error" => new PaymentUnavailableException(),
            _ => null,
        };
    }
}
