using System.Text.Json.Serialization;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Payments.Stripe;
using LeaseBook.Web.Portal;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LeaseBook.Web.Payments;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitPaymentBody(Guid Key, decimal Amount, string Currency,
    string Method = PaymentMethods.Ach, decimal QuotedFee = 0m);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClosePaymentReviewBody(string Note);
public sealed record UnmatchedObservationsResponse(IReadOnlyList<UnmatchedObservationView> Items);
public sealed record SettlementsResponse(IReadOnlyList<SettlementView> Items);
/// <param name="FundsInTransit">
/// Ledger amounts collected by the processor and not yet at the bank (ADR-053): the caller's own for a
/// tenant, the organization's for staff. A figure beside the balance, never part of it.
/// </param>
public sealed record PaymentsResponse(bool Enabled, IReadOnlyList<PaymentView> Items, int UnmatchedObservations = 0,
    decimal FundsInTransit = 0m);

public sealed class PaymentEndpoints : IEndpointModule
{
    /// <summary>The rate limit policy of a processor's own callback route, apart from the one tenants and staff share.</summary>
    public const string CallbackRateLimit = "payment-callbacks";

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var settings = app.ServiceProvider.GetRequiredService<SimulationSettings>();
        var tenant = app.MapGroup("/api/portal/tenant/payments").WithTags("Simulated payments")
            .RequireAuthorization(AuthPolicies.RequireTenant).AddEndpointFilter<ResidentAccessFilter>();
        tenant.MapGet("", async (CurrentResident resident, IOrgContext org, ISender sender, PaymentEngine engine, CancellationToken ct) =>
        {
            var binding = settings.ForOrg(org.OrgId);
            if (binding is null) { return TypedResults.Ok(new PaymentsResponse(false, [])); }
            await engine.RequireFixtureAsync(binding, ct);
            var tenantId = resident.Identity!.TenantId;
            return TypedResults.Ok(new PaymentsResponse(true, await sender.Query(new GetPayments(tenantId), ct),
                FundsInTransit: (await sender.Query(new GetFundsInTransit([tenantId]), ct)).Sum(x => x.Amount)));
        });
        tenant.MapGet("/{id:guid}", async Task<Results<Ok<PaymentView>, NotFound>> (Guid id,
            CurrentResident resident, IOrgContext org, PaymentEngine engine, ISender sender, CancellationToken ct) =>
        {
            var binding = settings.ForOrg(org.OrgId);
            if (binding is null) { return TypedResults.NotFound(); }
            await engine.RequireFixtureAsync(binding, ct);
            var result = (await sender.Query(new GetPayments(resident.Identity!.TenantId, id), ct)).SingleOrDefault();
            return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
        });

        var staff = app.MapGroup("/api/payments").WithTags("Simulated payments").RequireAuthorization(AuthPolicies.RequirePMStaff);
        staff.MapGet("", async (IOrgContext org, ISender sender, PaymentEngine engine, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var binding = settings.ForOrg(org.OrgId);
            if (binding is null) { return TypedResults.Ok(new PaymentsResponse(false, [])); }
            await engine.RequireFixtureAsync(binding, ct);
            return TypedResults.Ok(new PaymentsResponse(true, await sender.Query(new GetPayments(null), ct),
                await sender.Query(new GetUnmatchedPaymentObservations(), ct),
                (await sender.Query(new GetFundsInTransit(), ct)).Sum(x => x.Amount)));
        });
        // OpenAPI generation maps the contract without activating any simulator infrastructure.
        if (!settings.Enabled && Environment.GetEnvironmentVariable("LEASEBOOK_OPENAPI_BUILD") != "1") { return; }
        tenant.MapPost("", async Task<Results<Accepted<PaymentView>, NotFound>> (SubmitPaymentBody body,
            CurrentResident resident, IOrgContext org, IActorContext actor, ISender sender, CancellationToken ct) =>
        {
            var binding = settings.ForOrg(org.OrgId);
            if (binding is null) { return TypedResults.NotFound(); }
            var result = await sender.Send(new SubmitPayment(binding, resident.Identity!.TenantId,
                actor.UserId!.Value, body.Key, body.Amount, body.Currency, body.Method, body.QuotedFee), ct);
            return TypedResults.Accepted($"/api/portal/tenant/payments/{result.Id}", result);
        }).RequireRateLimiting("payments");

        // The fee is quoted by the server and confirmed by the tenant: submit refuses a fee that is no
        // longer the organization's, so what is charged is always what was shown.
        tenant.MapGet("/quote", async Task<Results<Ok<PaymentQuoteView>, NotFound>> (decimal amount, string method,
            IOrgContext org, ISender sender, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var binding = settings.ForOrg(org.OrgId);
            return binding is null ? TypedResults.NotFound()
                : TypedResults.Ok(await sender.Query(new GetPaymentQuote(binding, amount, method), ct));
        }).RequireRateLimiting("payments");

        staff.MapPost("/{id:guid}/retry", async Task<Results<NoContent, NotFound>> (Guid id,
            IOrgContext org, PaymentEngine engine, CancellationToken ct) =>
        {
            var binding = settings.ForOrg(org.OrgId);
            return binding is not null && await engine.RetryAsync(binding, id, ct)
                ? TypedResults.NoContent() : TypedResults.NotFound();
        }).RequireAuthorization(AuthPolicies.RequirePMAdmin).RequireRateLimiting("payments");

        // Mapped only with the simulation: without it there is no inbox to read.
        staff.MapGet("/unmatched", async Task<Results<Ok<UnmatchedObservationsResponse>, NotFound>> (
            IOrgContext org, ISender sender, PaymentEngine engine, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var binding = settings.ForOrg(org.OrgId);
            if (binding is null) { return TypedResults.NotFound(); }
            await engine.RequireFixtureAsync(binding, ct);
            return TypedResults.Ok(new UnmatchedObservationsResponse(
                await sender.Query(new GetUnmatchedPaymentObservationList(), ct)));
        });

        // Payout batches (ADR-053). Like a refused return, a payout that still cannot post is answered
        // as a 409 RESULT so that the reason it waits is committed.
        staff.MapGet("/settlements", async Task<Results<Ok<SettlementsResponse>, NotFound>> (
            IOrgContext org, ISender sender, PaymentEngine engine, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var binding = settings.ForOrg(org.OrgId);
            if (binding is null) { return TypedResults.NotFound(); }
            await engine.RequireFixtureAsync(binding, ct);
            return TypedResults.Ok(new SettlementsResponse(await sender.Query(new GetSettlements(), ct)));
        });

        staff.MapPost("/settlements/{id:guid}/post", async Task<Results<Ok<SettlementView>, NotFound, ProblemHttpResult>> (
            Guid id, IOrgContext org, IActorContext actor, ISender sender, HttpContext http, CancellationToken ct) =>
        {
            var binding = settings.ForOrg(org.OrgId);
            if (binding is null || await sender.Send(new PostSettlement(binding, id, actor.UserId!.Value), ct) is not { } result)
            { return TypedResults.NotFound(); }
            return result.Refusal is { } refusal
                ? ProblemResults.TypedProblem(http, refusal, SettlementReviewResult.Describe(refusal), StatusCodes.Status409Conflict)
                : TypedResults.Ok(result.Settlement);
        }).RequireAuthorization(AuthPolicies.RequirePMAdmin).RequireRateLimiting("payments");

        staff.MapPost("/settlements/{id:guid}/close", async Task<Results<Ok<SettlementView>, NotFound>> (Guid id,
            ClosePaymentReviewBody body, IOrgContext org, IActorContext actor, ISender sender, CancellationToken ct) =>
        {
            var binding = settings.ForOrg(org.OrgId);
            return binding is not null && await sender.Send(
                new CloseSettlementReview(binding, id, actor.UserId!.Value, body.Note), ct) is { } result
                ? TypedResults.Ok(result.Settlement) : TypedResults.NotFound();
        }).RequireAuthorization(AuthPolicies.RequirePMAdmin).RequireRateLimiting("payments");

        // A refused return is answered as a 409 RESULT, not an exception: the request transaction then
        // commits, which is what keeps the reason the payment stays in review.
        staff.MapPost("/{id:guid}/return", async Task<Results<Ok<PaymentView>, NotFound, ProblemHttpResult>> (Guid id,
            IOrgContext org, ISender sender, HttpContext http, CancellationToken ct) =>
        {
            var binding = settings.ForOrg(org.OrgId);
            if (binding is null || await sender.Send(new PostPaymentReturn(binding, id), ct) is not { } result)
            { return TypedResults.NotFound(); }
            return result.Refusal is { } refusal
                ? ProblemResults.TypedProblem(http, refusal, PaymentReviewResult.Describe(refusal), StatusCodes.Status409Conflict)
                : TypedResults.Ok(result.Payment);
        }).RequireAuthorization(AuthPolicies.RequirePMAdmin).RequireRateLimiting("payments");

        staff.MapPost("/{id:guid}/close-review", async Task<Results<Ok<PaymentView>, NotFound>> (Guid id,
            ClosePaymentReviewBody body, IOrgContext org, IActorContext actor, ISender sender, CancellationToken ct) =>
        {
            var binding = settings.ForOrg(org.OrgId);
            return binding is not null && await sender.Send(
                new ClosePaymentReview(binding, id, actor.UserId!.Value, body.Note), ct) is { } result
                ? TypedResults.Ok(result.Payment) : TypedResults.NotFound();
        }).RequireAuthorization(AuthPolicies.RequirePMAdmin).RequireRateLimiting("payments");

        // Outside cookie-authenticated /api: signature authentication replaces CSRF. These handlers
        // ignore all cookies and create their own org scope only AFTER verified server-side routing.
        // A host has one processor, and only that processor's routes: a callback route whose
        // processor is not registered could do nothing but refuse, so it does not exist. The
        // simulator's two are also what the documented contract holds, built with payments disabled.
        if (settings.Simulated || !settings.Enabled)
        {
            app.MapPost("/callbacks/payments/simulation", Receive).AllowAnonymous().RequireRateLimiting("payments")
                .WithTags("Simulated payment callbacks").Produces(204).ProducesProblem(400);
            // Payout evidence (ADR-053): the bank amount for one payout and the processor's lines for it.
            app.MapPost("/callbacks/payments/simulation/payout", ReceivePayout).AllowAnonymous().RequireRateLimiting("payments")
                .WithTags("Simulated payment callbacks").Produces(204).ProducesProblem(400);
        }
        // Stripe's events (ADR-054). Not part of the documented contract: nothing of LeaseBook's calls it.
        if (settings.Mode == PaymentModes.StripeSandbox)
        {
            app.MapPost("/callbacks/payments/stripe", ReceiveStripe).AllowAnonymous().RequireRateLimiting(CallbackRateLimit)
                .WithTags("Stripe sandbox payment callbacks").Produces(204).ProducesProblem(400);
        }
    }

    private static Task<IResult> Receive(HttpContext http, IPaymentProcessor processor, PaymentRunner runner, CancellationToken ct) =>
        ReceiveAsync(http, processor, runner, "X-Simulation-Signature", SimulationBodyLimit, ct);

    // Two limits on purpose. This one stops a larger body being read into memory at all; the adapter's
    // own, in Authenticate, refuses one however it arrived. Each alone gives the same answer, so only
    // the adapter's has a test that fails without it.
    private static Task<IResult> ReceiveStripe(HttpContext http, IPaymentProcessor processor, PaymentRunner runner, CancellationToken ct) =>
        ReceiveAsync(http, processor, runner, "Stripe-Signature", StripeSandboxProcessor.MaxNoticeBytes, ct);

    // One flow for every processor: the body whole and within its limit, proved authentic, read, and
    // only then routed to an organization. What differs is the header the proof arrives in and the limit.
    private static async Task<IResult> ReceiveAsync(HttpContext http, IPaymentProcessor processor, PaymentRunner runner,
        string signatureHeader, int limit, CancellationToken ct)
    {
        if (await ReadBodyAsync(http, limit, ct) is not { } body) { return Invalid(http); }
        if (processor.Authenticate(body, http.Request.Headers[signatureHeader].ToString()) is not { } notice) { return Invalid(http); }
        var read = await processor.ReadObservationAsync(notice, ct);
        if (read.Ignored) { runner.Ignore(); return TypedResults.NoContent(); }
        if (read.Value is not { } observation) { return Invalid(http); }
        await runner.ReceiveAsync(observation, ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ReceivePayout(HttpContext http, IPaymentProcessor processor, PaymentRunner runner, CancellationToken ct)
    {
        if (await ReadBodyAsync(http, SimulationBodyLimit, ct) is not { } body) { return Invalid(http); }
        if (processor.Authenticate(body, http.Request.Headers["X-Simulation-Signature"].ToString()) is not { } notice) { return Invalid(http); }
        var read = await processor.ReadSettlementAsync(notice, ct);
        if (read.Ignored) { runner.Ignore(); return TypedResults.NoContent(); }
        if (read.Value is not { } evidence) { return Invalid(http); }
        // Evidence that cannot be kept as it arrived was not delivered, and the sender is told so. A
        // payout that was kept is acknowledged whatever became of it: held payouts are staff's to resolve.
        return (await runner.ReceiveSettlementAsync(evidence, ct)).Malformed ? Invalid(http) : TypedResults.NoContent();
    }

    private const int SimulationBodyLimit = 16384;

    // The whole body, or null when it is larger than this route's callbacks may be.
    private static async Task<byte[]?> ReadBodyAsync(HttpContext http, int limit, CancellationToken ct)
    {
        if (http.Request.ContentLength > limit) { return null; }
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await http.Request.Body.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > limit) { return null; }
            buffer.Write(bytes, 0, count);
        }
        return buffer.ToArray();
    }

    private static IResult Invalid(HttpContext http) => ProblemResults.Problem(http, "invalid_payment_callback",
        "The payment notification could not be verified.", StatusCodes.Status400BadRequest);
}
