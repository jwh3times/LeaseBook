using System.Text.Json.Serialization;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Portal;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LeaseBook.Web.Payments;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitPaymentBody(Guid Key, decimal Amount, string Currency);
public sealed record PaymentsResponse(bool Enabled, IReadOnlyList<PaymentView> Items, int UnmatchedObservations = 0);

public sealed class PaymentEndpoints : IEndpointModule
{
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
            return TypedResults.Ok(new PaymentsResponse(true, await sender.Query(new GetPayments(resident.Identity!.TenantId), ct)));
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
                await sender.Query(new GetUnmatchedPaymentObservations(), ct)));
        });
        // OpenAPI generation maps the contract without activating any simulator infrastructure.
        if (!settings.Enabled && Environment.GetEnvironmentVariable("LEASEBOOK_OPENAPI_BUILD") != "1") { return; }
        tenant.MapPost("", async Task<Results<Accepted<PaymentView>, NotFound>> (SubmitPaymentBody body,
            CurrentResident resident, IOrgContext org, IActorContext actor, ISender sender, CancellationToken ct) =>
        {
            var binding = settings.ForOrg(org.OrgId);
            if (binding is null) { return TypedResults.NotFound(); }
            var result = await sender.Send(new SubmitPayment(binding, resident.Identity!.TenantId,
                actor.UserId!.Value, body.Key, body.Amount, body.Currency), ct);
            return TypedResults.Accepted($"/api/portal/tenant/payments/{result.Id}", result);
        }).RequireRateLimiting("payments");

        staff.MapPost("/{id:guid}/retry", async Task<Results<NoContent, NotFound>> (Guid id,
            IOrgContext org, PaymentEngine engine, CancellationToken ct) =>
        {
            var binding = settings.ForOrg(org.OrgId);
            return binding is not null && await engine.RetryAsync(binding, id, ct)
                ? TypedResults.NoContent() : TypedResults.NotFound();
        }).RequireAuthorization(AuthPolicies.RequirePMAdmin).RequireRateLimiting("payments");

        // Outside cookie-authenticated /api: signature authentication replaces CSRF. This handler
        // ignores all cookies and creates its own org scope only AFTER verified server-side routing.
        app.MapPost("/callbacks/payments/simulation", Receive).AllowAnonymous().RequireRateLimiting("payments")
            .WithTags("Simulated payment callbacks").Produces(204).ProducesProblem(400);
    }

    private static async Task<IResult> Receive(HttpContext http, IPaymentProcessor processor, PaymentRunner runner, CancellationToken ct)
    {
        if (http.Request.ContentLength > 16384) { return Invalid(http); }
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await http.Request.Body.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > 16384) { return Invalid(http); }
            buffer.Write(bytes, 0, count);
        }
        var observation = processor.VerifyAndNormalize(buffer.ToArray(), http.Request.Headers["X-Simulation-Signature"].ToString());
        if (observation is null) { return Invalid(http); }
        await runner.ReceiveAsync(observation, ct);
        return TypedResults.NoContent();
    }

    private static IResult Invalid(HttpContext http) => ProblemResults.Problem(http, "invalid_payment_callback",
        "The payment notification could not be verified.", StatusCodes.Status400BadRequest);
}
