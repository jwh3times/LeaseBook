using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// own; nothing that identifies a tenant. Of Stripe's events it reads how a payment went: processing,
/// succeeded or failed. None of those is money at a bank, and payouts are not read yet.
/// </summary>
public sealed class StripeSandboxProcessor : IPaymentProcessor
{
    private const string OperationKey = "leasebook_operation";
    private const string GenerationKey = "leasebook_generation";
    // Stripe may forget an idempotency key a day after it first saw it. Past this age a repeat could
    // be taken as a new charge, so nothing is sent and a person looks instead.
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(23);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    /// <summary>The largest callback body read. Stripe's events are several times the simulator's notices; the largest recorded is under 5 KB.</summary>
    public const int MaxNoticeBytes = 64 * 1024;
    private const int PageSize = 100;
    private const int MaxPages = 100;
    // How far a signature's time may be from this host's clock, either way. Stripe's own default.
    private const int SignatureToleranceSeconds = 300;
    private const int MaxSignatureHeader = 1024;
    // What a stored id and a stored amount can hold: 100 characters, and NUMERIC(14,2) in cents.
    private const int MaxIdLength = 100;
    private const long MaxMinorUnits = 99_999_999_999_999;
    private const long MaxUnixSeconds = 253402300799;

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
        var account = new RequestOptions { StripeAccount = request.Binding.Account };
        // A reference LeaseBook stored names the payment, so that payment is asked for and nothing is
        // searched: an event that wakes an old operation costs one request, not a list of everything
        // on the account since the operation was created.
        if (!string.IsNullOrEmpty(request.ProviderId)) { return await RetrieveAsync(request, request.ProviderId, amount, account, ct); }
        var options = new PaymentIntentListOptions
        {
            Created = new DateRangeOptions { GreaterThanOrEqual = Created(request) - ClockSkew },
            Limit = PageSize,
        };
        var found = new List<PaymentIntent>();
        for (var page = 0; ; page++)
        {
            // Not an answer either way: stopping here as "absent" would let the charge be made twice.
            if (page == MaxPages) { throw new IOException("The Stripe sandbox listed more payments than a lookup reads."); }
            // A read charges nothing, so no refusal of one says anything about the payment: whatever
            // Stripe answers but a list is left to be retried, and after that for staff to retry.
            var list = await _client.V1.PaymentIntents.ListAsync(options, account, ct);
            if (list.Data.Any(x => x.Livemode)) { throw new PaymentUnavailableException(); }
            found.AddRange(list.Data.Where(x => MadeFor(x, request)));
            if (!list.HasMore || list.Data.Count == 0) { break; }
            options.StartingAfter = list.Data[^1].Id;
        }
        if (found.Count == 0) { return new ProcessorResult("Absent", null); }
        // One payment, and the one that was asked for. Anything else under this operation's id is
        // evidence that disagrees with what LeaseBook holds, which a person has to read.
        if (found.Count > 1 || !AsAsked(found[0], request, amount)) { throw new PaymentConflictException(); }
        return Accepted(found[0]);
    }

    // The payment a stored reference names, held to everything a payment found by the scan is held
    // to. Whatever Stripe answers but that payment is left as it is thrown, to be retried, except
    // that it has none: LeaseBook then holds a reference to a payment that does not exist, which is
    // evidence that disagrees and never grounds to search for another or to charge again.
    private async Task<ProcessorResult> RetrieveAsync(ProcessorRequest request, string reference, long amount, RequestOptions account, CancellationToken ct)
    {
        PaymentIntent intent;
        try { intent = await _client.V1.PaymentIntents.GetAsync(reference, null, account, ct); }
        catch (StripeException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound) { throw new PaymentConflictException(); }
        if (intent.Livemode) { throw new PaymentUnavailableException(); }
        if (!string.Equals(intent.Id, reference, StringComparison.Ordinal) || !MadeFor(intent, request) || !AsAsked(intent, request, amount))
        { throw new PaymentConflictException(); }
        return Accepted(intent);
    }

    private static bool MadeFor(PaymentIntent intent, ProcessorRequest request) => intent.Metadata is not null
        && intent.Metadata.TryGetValue(OperationKey, out var operation)
        && Guid.TryParse(operation, out var id) && id == request.OperationId;

    private static bool AsAsked(PaymentIntent intent, ProcessorRequest request, long amount) => intent.Amount == amount
        && string.Equals(intent.Currency, "usd", StringComparison.Ordinal)
        && intent.Metadata is not null && intent.Metadata.TryGetValue(GenerationKey, out var generation)
        && Guid.TryParse(generation, out var parsed) && parsed == request.Binding.Generation;

    public ProcessorNotice? Authenticate(byte[] rawBody, string signature) =>
        Signed(rawBody, signature) ? new ProcessorNotice(rawBody) : null;

    // Stripe's scheme: "t=<unix seconds>,v1=<hex>[,v1=<hex>]", each v1 an HMAC-SHA256 of
    // "<t>.<body>" under the endpoint's signing secret; one per secret while a secret is rolled.
    // Recent by this host's clock, and small enough to have been read whole. Nothing in the body is
    // parsed, and no part of the header is trusted, before a signature matches.
    private bool Signed(byte[] rawBody, string signature)
    {
        if (_settings.Mode != PaymentModes.StripeSandbox || _settings.Stripe?.WebhookSecret is not { Length: > 0 } secret
            || rawBody.Length > MaxNoticeBytes || signature is null || signature.Length is 0 or > MaxSignatureHeader)
        { return false; }
        string? timestamp = null;
        var presented = new List<byte[]>();
        foreach (var part in signature.Split(','))
        {
            var at = part.IndexOf('=');
            if (at <= 0) { return false; }
            var value = part[(at + 1)..];
            switch (part[..at])
            {
                case "t":
                    if (timestamp is not null) { return false; }
                    timestamp = value;
                    break;
                case "v1":
                    // A signature is 32 bytes of hex or it is not Stripe's header.
                    if (value.Length != 64) { return false; }
                    try { presented.Add(Convert.FromHexString(value)); } catch (FormatException) { return false; }
                    break;
                default:
                    // Any other scheme proves nothing here and is passed over, as Stripe's own libraries do.
                    break;
            }
        }
        if (timestamp is null || presented.Count == 0
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > MaxUnixSeconds
            || Math.Abs((_clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds)).TotalSeconds) > SignatureToleranceSeconds)
        { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), (byte[])[.. Encoding.UTF8.GetBytes(timestamp + "."), .. rawBody]);
        // Every one is compared, in constant time each, so that how long this takes says nothing of which matched.
        var matched = false;
        foreach (var candidate in presented) { matched |= CryptographicOperations.FixedTimeEquals(candidate, expected); }
        return matched;
    }

    // A snapshot event carries the payment it is about, so reading one asks Stripe nothing.
    public Task<ProcessorRead<ProcessorObservation>> ReadObservationAsync(ProcessorNotice notice, CancellationToken ct) =>
        Task.FromResult(Observation(notice.Body));

    // Payouts are read in a later step. Until then no delivery is payout evidence.
    public Task<ProcessorRead<ProcessorSettlement>> ReadSettlementAsync(ProcessorNotice notice, CancellationToken ct) =>
        Task.FromResult(ProcessorRead<ProcessorSettlement>.NotOurs);

    /// <summary>
    /// What an authentic event says of one payment. Read member by member and not into the library's
    /// types, which throw on a shape or an API version they do not expect. In this order: an event
    /// that is not one, or is marked live, is unreadable whoever it is for; one for an account no
    /// fixture here is bound to, of another kind, or about a payment of another fixture generation is
    /// not this host's; and one that is this host's and cannot be taken as it stands is unreadable.
    /// The generation and bank on the result are the binding's. Nothing read here is bank evidence:
    /// it has no evidence id, no payout and is never complete.
    /// </summary>
    private ProcessorRead<ProcessorObservation> Observation(byte[] body)
    {
        var unreadable = ProcessorRead<ProcessorObservation>.Of(null);
        var notOurs = ProcessorRead<ProcessorObservation>.NotOurs;
        JsonDocument document;
        try { document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false }); }
        catch (JsonException) { return unreadable; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Id(root, "id") is not { } eventId || Text(root, "type") is not { } type
                || !root.TryGetProperty("created", out var created) || created.ValueKind != JsonValueKind.Number
                || !created.TryGetInt64(out var seconds) || seconds is <= 0 or > MaxUnixSeconds)
            { return unreadable; }
            // The event's own flag is the one that counts, and it must be there and exactly false: a
            // live event is refused, not acknowledged, and so is an event that does not say.
            if (!root.TryGetProperty("livemode", out var live) || live.ValueKind != JsonValueKind.False) { return unreadable; }
            // The platform's own events name no account. Routing reads the host's bindings and nothing
            // else: no organization is entered and none is read to decide that an event is not ours.
            if (Text(root, "account") is not { } account || _settings.ForAccount(account) is not { } binding) { return notOurs; }
            var kind = type switch
            {
                "payment_intent.processing" => "Processing",
                "payment_intent.succeeded" => "Succeeded",
                "payment_intent.payment_failed" => "Failed",
                _ => null,
            };
            if (kind is null) { return notOurs; }
            // The payment inside carries the flag too. It may be absent, the event having answered; when
            // it is there it must also be exactly false.
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("object", out var payment) || payment.ValueKind != JsonValueKind.Object
                || Text(payment, "object") != "payment_intent" || Id(payment, "id") is not { } providerId
                || (payment.TryGetProperty("livemode", out var paymentLive) && paymentLive.ValueKind != JsonValueKind.False))
            { return unreadable; }
            // The account also holds payments of the probe and of older fixture generations. They are
            // authentic and not this host's, whatever else is true of them.
            if (!payment.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object
                || !Guid.TryParse(Text(metadata, GenerationKey), out var generation) || generation != binding.Generation
                || !Guid.TryParse(Text(metadata, OperationKey), out _))
            { return notOurs; }
            if (Text(payment, "currency") != "usd" || !payment.TryGetProperty("amount", out var amount)
                || amount.ValueKind != JsonValueKind.Number || !amount.TryGetInt64(out var minor) || minor is <= 0 or > MaxMinorUnits)
            { return unreadable; }
            var at = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            var charged = minor / 100m;
            return ProcessorRead<ProcessorObservation>.Of(new ProcessorObservation(eventId, providerId, binding.Account, binding.Mode,
                binding.Generation, kind, charged, 0m, charged, "USD", binding.BankId, DateOnly.FromDateTime(at),
                EvidenceId: "", PayoutId: "", Complete: false, at));
        }
    }

    private static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // An id as it is stored: text, not empty, and no longer than its column.
    private static string? Id(JsonElement owner, string name) => Text(owner, name) is { Length: > 0 and <= MaxIdLength } id ? id : null;

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
    private static bool Live(Account account) => account.RawJsonElement is { ValueKind: JsonValueKind.Object } raw
        && raw.TryGetProperty("livemode", out var live) && live.ValueKind == JsonValueKind.True;

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
