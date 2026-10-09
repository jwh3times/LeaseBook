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
/// succeeded or failed; and what became of it afterwards: refunded, or disputed, which for an ACH
/// debit is how Stripe reports a return. None of those is money at a bank or money leaving one, so
/// nothing read here can post, and payouts are not read yet. It never refunds and never answers a dispute.
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
        // The checks above bound what is handed on and keep the clock this host's own. Whether a
        // signature matches is Stripe's library's to say (the specification requires its
        // verification), on the same clock and tolerance. It reads text, so the body must be exactly
        // the UTF-8 it claims to be: bytes that are not would be signed as something else.
        string text;
        try { text = StrictUtf8.GetString(rawBody); } catch (DecoderFallbackException) { return false; }
        try
        {
            EventUtility.ValidateSignature(text, signature, secret, SignatureToleranceSeconds, _clock.GetUtcNow().ToUnixTimeSeconds());
            return true;
        }
        catch (StripeException) { return false; }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // An event that carries the payment it is about is read without asking Stripe anything. A dispute
    // carries nothing of LeaseBook's, so reading one asks for the payment and the charge it names.
    public async Task<ProcessorRead<ProcessorObservation>> ReadObservationAsync(ProcessorNotice notice, CancellationToken ct)
    {
        try { return await ObservationAsync(notice.Body, only: null, [], ct); }
        // A test event about a payment or a charge Stripe marks live: refused, as an event marked live is.
        catch (PaymentUnavailableException) { return ProcessorRead<ProcessorObservation>.Of(null); }
    }

    // Payouts are read in a later step. Until then no delivery is payout evidence.
    public Task<ProcessorRead<ProcessorSettlement>> ReadSettlementAsync(ProcessorNotice notice, CancellationToken ct) =>
        Task.FromResult(ProcessorRead<ProcessorSettlement>.NotOurs);

    /// <summary>
    /// Lists the events Stripe holds for one fixture's account since a time, for one that was never
    /// delivered, and reads each as a delivery is read. Nothing listed proves itself with a signature.
    /// The trust is of another kind: the list was asked of Stripe, with the platform's key, over TLS,
    /// for one named account. So no notice is made of it, and an event counts only for the account
    /// that was asked about. The list is taken as text and read member by member, for the reason the
    /// reader gives; the library's own list would put every event through its types first. An event
    /// the reader must ask Stripe about is asked about here too, so that it is read as it was when
    /// delivered. One that cannot be asked about is counted and passed over, so that it does not cost
    /// the sweep what it could read; the caller asks for it again. An answer marked live fails the sweep.
    /// </summary>
    public async Task<ProcessorRecovery> RecoverObservationsAsync(FixtureBinding binding, DateTime since, CancellationToken ct)
    {
        if (_settings.ForOrg(binding.OrgId) != binding) { throw new PaymentUnavailableException(); }
        var from = new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc) - ClockSkew).ToUnixTimeSeconds();
        // Only the types the reader maps: an account's other events are several times as many.
        var query = string.Create(CultureInfo.InvariantCulture, $"/v1/events?created[gte]={from}&limit={PageSize}")
            + string.Concat(Events.Select((x, i) => string.Create(CultureInfo.InvariantCulture, $"&types[{i}]={x.Type}")));
        var account = new RawRequestOptions { StripeAccount = binding.Account };
        var found = new List<ProcessorObservation>();
        // What this sweep has asked Stripe so far. A dispute's events name one payment and one charge.
        var asked = new Dictionary<string, string?>(StringComparer.Ordinal);
        var (listed, unreadable, unasked) = (0, 0, 0);
        string? after = null;
        for (var page = 0; ; page++)
        {
            // Not an answer: stopping here would say that every event since then had been asked for.
            if (page == MaxPages) { throw new IOException("The Stripe sandbox listed more events than a sweep reads."); }
            // A read says nothing about any payment: whatever Stripe answers but a list is left as
            // thrown, and the sweep is made again later.
            var response = await _client.RawRequestAsync(HttpMethod.Get,
                after is null ? query : query + "&starting_after=" + Uri.EscapeDataString(after), null, account, ct);
            JsonDocument document;
            try { document = JsonDocument.Parse(response.Content); }
            catch (JsonException) { throw NotAList(); }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
                    || !root.TryGetProperty("has_more", out var more) || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { throw NotAList(); }
                // Anywhere in the list and whatever it is about, as for a lookup: the key is reading
                // a live account, and nothing it lists is kept, the pages before this one included.
                if (data.EnumerateArray().Any(MarkedLive)) { throw new PaymentUnavailableException(); }
                foreach (var item in data.EnumerateArray())
                {
                    listed++;
                    // The reader routes by any binding of this host. Here only one account was asked.
                    ProcessorRead<ProcessorObservation> read;
                    try { read = await ObservationAsync(Encoding.UTF8.GetBytes(item.GetRawText()), binding, asked, ct); }
                    // Stripe would not say what the event is about. Nothing is known of it, so it is
                    // neither kept nor called unreadable; the events beside it are not held up by it.
                    catch (Exception ex) when (ex is not PaymentUnavailableException && !ct.IsCancellationRequested) { unasked++; continue; }
                    // One that cannot be read is counted and passed over: it must not hide the rest.
                    if (read.Value is null) { unreadable += read.Ignored ? 0 : 1; continue; }
                    found.Add(read.Value);
                }
                if (more.ValueKind == JsonValueKind.False) { break; }
                // More to come and nothing to say where from: not the end, so not an answer.
                if (data.GetArrayLength() == 0) { throw NotAList(); }
                after = Id(data[data.GetArrayLength() - 1], "id") ?? throw NotAList();
            }
        }
        return new ProcessorRecovery(found, listed, unreadable, unasked);
    }

    private static IOException NotAList() => new("The Stripe sandbox did not answer with a list of events.");

    // Marked live on the event or on the payment inside it. Only what says so: an event that does
    // not say is the reader's to refuse, by itself.
    private static bool MarkedLive(JsonElement listed) => listed.ValueKind == JsonValueKind.Object
        && ((listed.TryGetProperty("livemode", out var live) && live.ValueKind == JsonValueKind.True)
            || (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("object", out var inner) && inner.ValueKind == JsonValueKind.Object
                && inner.TryGetProperty("livemode", out var innerLive) && innerLive.ValueKind == JsonValueKind.True));

    // The events read: what kind of object each carries, and what it says of a payment. The reader
    // and the sweep's filter both take them from here, so that a sweep asks for exactly what a
    // delivery would be read as. Stripe takes at most twenty types in one filter.
    private static readonly (string Type, string About, string Kind)[] Events =
    [
        ("payment_intent.processing", "payment_intent", "Processing"),
        ("payment_intent.succeeded", "payment_intent", "Succeeded"),
        ("payment_intent.payment_failed", "payment_intent", "Failed"),
        // The charge as a refund left it, which carries the payment's metadata. A refund's other
        // events carry the refund, which has none, and say nothing this one does not.
        ("charge.refunded", "charge", "Refund"),
        // A dispute, and a "Return" instead when the charge it names was an ACH debit. No other
        // dispute event has been recorded, so no other is read.
        ("charge.dispute.created", "dispute", "Dispute"),
        ("charge.dispute.funds_withdrawn", "dispute", "Dispute"),
        ("charge.dispute.closed", "dispute", "Dispute"),
    ];

    private enum Whose { Ours, NotOurs, Unreadable }

    /// <summary>
    /// What an authentic event says of one payment. Read member by member and not into the library's
    /// types, which throw on a shape or an API version they do not expect. In this order: an event
    /// that is not one, or is marked live, is unreadable whoever it is for; one for an account no
    /// fixture here is bound to (or, in a sweep, any account but the one asked), of another kind, or
    /// about a payment of another fixture generation is not this host's; and one that is this host's
    /// and cannot be taken as it stands is unreadable. The generation and bank on the result are the
    /// binding's. Nothing read here is bank evidence: it has no payout and is never complete.
    /// <para>
    /// A dispute says whose payment it is about only by Stripe's id, so that payment is asked for and
    /// put to the test a payment's own event gets. What cannot be asked is thrown, never taken as an
    /// answer; anything live in an answer throws <see cref="PaymentUnavailableException"/>. The events
    /// of one dispute carry the dispute's id and date, not their own, so that they agree with each other.
    /// </para>
    /// <para>
    /// An event for this fixture's payment in a currency that was not charged is kept as conflicting
    /// evidence, with the currency and amount it names. The amount is Stripe's count of the
    /// currency's smallest unit taken as hundredths, which is exact for a currency with two decimal
    /// places and not for one with none or three; it is evidence for a person and is never posted.
    /// </para>
    /// </summary>
    private async Task<ProcessorRead<ProcessorObservation>> ObservationAsync(byte[] body, FixtureBinding? only,
        Dictionary<string, string?> asked, CancellationToken ct)
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
                || Seconds(root, "created") is not { } seconds)
            { return unreadable; }
            // The event's own flag is the one that counts, and it must be there and exactly false: a
            // live event is refused, not acknowledged, and so is an event that does not say.
            if (!root.TryGetProperty("livemode", out var live) || live.ValueKind != JsonValueKind.False) { return unreadable; }
            // The platform's own events name no account. Routing reads the host's bindings and nothing
            // else: no organization is entered and none is read to decide that an event is not ours.
            if (Text(root, "account") is not { } account || _settings.ForAccount(account) is not { } binding
                || (only is not null && binding != only))
            { return notOurs; }
            var read = Events.FirstOrDefault(x => x.Type == type);
            if (read.Type is null) { return notOurs; }
            // What the event is about carries the flag too. It may be absent, the event having
            // answered; when it is there it must also be exactly false.
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("object", out var about) || about.ValueKind != JsonValueKind.Object
                || Text(about, "object") != read.About || Id(about, "id") is not { } aboutId
                || (about.TryGetProperty("livemode", out var aboutLive) && aboutLive.ValueKind != JsonValueKind.False))
            { return unreadable; }
            var at = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            var (providerId, evidenceId, kind, date, amountName) = (aboutId, "", read.Kind, DateOnly.FromDateTime(at), "amount");
            switch (read.About)
            {
                // The account also holds payments of the probe and of older fixture generations. They
                // are authentic and not this host's, whatever else is true of them.
                case "payment_intent":
                    if (!MadeHere(about, binding)) { return notOurs; }
                    break;
                // The amount refunded is the total so far, so a second refund of one charge says more than the first.
                case "charge":
                    if (!MadeHere(about, binding)) { return notOurs; }
                    if (Id(about, "payment_intent") is not { } refunded) { return unreadable; }
                    (providerId, evidenceId, amountName) = (refunded, aboutId, "amount_refunded");
                    break;
                default:
                    // Only a payment LeaseBook made can be its own, and it makes no charge without one.
                    if (!about.TryGetProperty("payment_intent", out var named) || named.ValueKind == JsonValueKind.Null) { return notOurs; }
                    if (Named(about, "payment_intent") is not { } disputed) { return unreadable; }
                    switch (await WhoseAsync(disputed, binding, asked, ct))
                    {
                        case Whose.NotOurs: return notOurs;
                        case Whose.Unreadable: return unreadable;
                        default: break;
                    }
                    if (Seconds(about, "created") is not { } opened) { return unreadable; }
                    (providerId, evidenceId, date) = (disputed, aboutId, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(opened).UtcDateTime));
                    break;
            }
            if (Text(about, "currency") is not { Length: 3 } currency || !currency.All(char.IsAsciiLetter)
                || !about.TryGetProperty(amountName, out var amount) || amount.ValueKind != JsonValueKind.Number
                || !amount.TryGetInt64(out var minor) || minor is <= 0 or > MaxMinorUnits)
            { return unreadable; }
            currency = currency.ToUpperInvariant();
            if (currency != "USD") { kind = PaymentEngine.ConflictKind; }
            // Asked last, and only of a dispute that is otherwise kept as one.
            else if (read.About == "dispute" && await AchDebitAsync(Named(about, "charge"), providerId, binding, asked, ct)) { kind = "Return"; }
            var said = minor / 100m;
            return ProcessorRead<ProcessorObservation>.Of(new ProcessorObservation(eventId, providerId, binding.Account, binding.Mode,
                binding.Generation, kind, said, 0m, said, currency, binding.BankId, date, evidenceId, PayoutId: "", Complete: false, at));
        }
    }

    // This fixture's own: made by LeaseBook for an operation, under the generation bound to the account.
    private static bool MadeHere(JsonElement made, FixtureBinding binding) =>
        made.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
        && Guid.TryParse(Text(metadata, GenerationKey), out var generation) && generation == binding.Generation
        && Guid.TryParse(Text(metadata, OperationKey), out _);

    // Whose payment a dispute is about, by the payment Stripe holds under the id the dispute names.
    // None there is nobody's here. One that is not the payment asked for, or does not say it is a
    // test, is no answer to rely on.
    private async Task<Whose> WhoseAsync(string payment, FixtureBinding binding, Dictionary<string, string?> asked, CancellationToken ct)
    {
        if (await AskAsync("/v1/payment_intents/" + payment, binding, asked, ct) is not { } answer) { return Whose.NotOurs; }
        using var document = Answer(answer);
        var held = document.RootElement;
        if (!held.TryGetProperty("livemode", out var live) || live.ValueKind != JsonValueKind.False
            || Text(held, "object") != "payment_intent" || Text(held, "id") != payment)
        { return Whose.Unreadable; }
        return MadeHere(held, binding) ? Whose.Ours : Whose.NotOurs;
    }

    // Whether a disputed charge was an ACH debit, which makes the dispute a bank's return of it. The
    // charge Stripe holds says so, under the id the dispute names and for the payment it names. The
    // dispute's own account of how it was paid is null for ACH, and an id's prefix is not a statement.
    // It is held to what the payment is held to: one that does not say it is a test is not read as
    // one. Whatever does not say so plainly leaves the dispute a dispute, which a person still reads.
    private async Task<bool> AchDebitAsync(string? charge, string payment, FixtureBinding binding, Dictionary<string, string?> asked, CancellationToken ct)
    {
        if (charge is null || await AskAsync("/v1/charges/" + charge, binding, asked, ct) is not { } answer) { return false; }
        using var document = Answer(answer);
        var held = document.RootElement;
        return held.TryGetProperty("livemode", out var live) && live.ValueKind == JsonValueKind.False
            && Text(held, "object") == "charge" && Text(held, "id") == charge && Text(held, "payment_intent") == payment
            && held.TryGetProperty("payment_method_details", out var details) && details.ValueKind == JsonValueKind.Object
            && Text(details, "type") == "us_bank_account";
    }

    // One object asked of Stripe by its id, on the fixture's account: its text, or null when Stripe
    // has none under that id. The path is all that is sent. Whatever else Stripe answers is left as
    // thrown: it says nothing about the object, and the event is read again another time.
    private async Task<string?> AskAsync(string path, FixtureBinding binding, Dictionary<string, string?> asked, CancellationToken ct)
    {
        if (asked.TryGetValue(path, out var known)) { return known; }
        string? answer;
        try { answer = (await _client.RawRequestAsync(HttpMethod.Get, path, null, new RawRequestOptions { StripeAccount = binding.Account }, ct)).Content; }
        catch (StripeException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound) { answer = null; }
        return asked[path] = answer;
    }

    // An answer as one object, with anything in it that is marked live refused before it is read.
    private static JsonDocument Answer(string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { throw NotAnObject(); }
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw NotAnObject(); }
        if (document.RootElement.TryGetProperty("livemode", out var live) && live.ValueKind == JsonValueKind.True)
        { document.Dispose(); throw new PaymentUnavailableException(); }
        return document;
    }

    private static IOException NotAnObject() => new("The Stripe sandbox did not answer with the object asked for.");

    private static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // An id as it is stored: text, not empty, and no longer than its column.
    private static string? Id(JsonElement owner, string name) => Text(owner, name) is { Length: > 0 and <= MaxIdLength } id ? id : null;

    // An id that can be asked for by name. Stripe's are letters, digits and underscores; anything
    // else could make the path it is put in another path, so it is never sent.
    private static string? Named(JsonElement owner, string name) =>
        Id(owner, name) is { } id && id.All(x => char.IsAsciiLetterOrDigit(x) || x == '_') ? id : null;

    // A time as Stripe gives one: whole seconds, after the epoch and within what a date can hold.
    private static long? Seconds(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var seconds) && seconds is > 0 and <= MaxUnixSeconds ? seconds : null;

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
