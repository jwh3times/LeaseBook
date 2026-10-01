using System.Text.Json.Serialization;
using FluentValidation;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Endpoints;

namespace LeaseBook.Web.Portal;

public sealed record PortalInvitationSummary(Guid Id, string Email, string Status, DateTimeOffset ExpiresAt, string DeliveryStatus);
public sealed record PortalAccessUser(Guid UserId, string Email, string? DisplayName);
public sealed record PortalAccessResponse(bool CanManage, IReadOnlyList<PortalInvitationSummary> Invitations, IReadOnlyList<PortalAccessUser> Users);
public sealed record PortalEnrollmentInspection(string Email, string DisplayName, string Persona, bool RequiresSignIn, bool RequiresPassword);
public sealed record PortalEnrollmentAccepted(string Persona);
public sealed record PortalAccessUpdated(bool Success = true);
public sealed record PortalTestMessage(Guid InvitationId, string Email, string AcceptUrl);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvitePortalUser(string Email);
public sealed class InvitePortalUserValidator : AbstractValidator<InvitePortalUser>
{
    public InvitePortalUserValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254).EmailAddress();
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InspectPortalInvitation(string Token);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptPortalInvitation(string Token, string? DisplayName = null, string? Password = null);

public sealed class PortalEnrollmentEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portal-access").WithTags("Portal access")
            .RequireAuthorization("RequirePMStaff").AddEndpointFilter<PortalEnrollmentFilter>();
        group.MapGet("/{persona}/{targetId:guid}", async (string persona, Guid targetId, PortalEnrollmentService service, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.ReadAsync(persona, targetId, http.User, ct))).Produces<PortalAccessResponse>();
        group.MapPost("/{persona}/{targetId:guid}", async (string persona, Guid targetId, InvitePortalUser body, PortalEnrollmentService service, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.InviteAsync(persona, targetId, body.Email, http.User, ct)))
            .AddEndpointFilter<ValidationEndpointFilter<InvitePortalUser>>().Produces<PortalInvitationSummary>();
        group.MapPost("/invitations/{id:guid}/replace", async (Guid id, PortalEnrollmentService service, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.ReplaceAsync(id, http.User, ct))).Produces<PortalInvitationSummary>();
        group.MapPost("/invitations/{id:guid}/cancel", async (Guid id, PortalEnrollmentService service, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.CancelAsync(id, http.User, ct))).Produces<PortalAccessUpdated>();
        group.MapPost("/invitations/{id:guid}/retry", async (Guid id, PortalEnrollmentService service, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.RetryAsync(id, http.User, ct))).Produces<PortalAccessUpdated>();
        group.MapPost("/{persona}/{targetId:guid}/users/{userId:guid}/revoke", async (string persona, Guid targetId, Guid userId, PortalEnrollmentService service, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.RevokeAsync(persona, targetId, userId, http.User, ct))).Produces<PortalAccessUpdated>();
        group.MapGet("/test-inbox", async (PortalEnrollmentService service, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.InboxAsync(http.User, ct))).Produces<IReadOnlyList<PortalTestMessage>>();

        var enrollment = app.MapGroup("/api/portal-enrollment").WithTags("Portal enrollment")
            .AllowAnonymous().RequireRateLimiting("auth").AddEndpointFilter<PortalEnrollmentFilter>();
        enrollment.MapPost("/inspect", async (InspectPortalInvitation body, PortalEnrollmentService service, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.InspectAsync(body.Token, http.User, ct))).Produces<PortalEnrollmentInspection>();
        enrollment.MapPost("/accept", async (AcceptPortalInvitation body, PortalEnrollmentService service, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.AcceptAsync(body, http.User, ct))).Produces<PortalEnrollmentAccepted>();
    }
}

public sealed class PortalEnrollmentException(string code, string detail, int status = 400) : Exception(detail)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed class PortalEnrollmentFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (PortalEnrollmentException e)
        {
            return ProblemResults.Problem(context.HttpContext, e.Code, e.Message, e.Status);
        }
    }
}
