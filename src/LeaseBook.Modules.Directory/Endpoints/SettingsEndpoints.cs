using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Modules.Directory.Endpoints;

/// <summary>
/// The settings surface (§C.4): org profile + basis/display preferences and trust bank accounts. Reads
/// are staff-level (<c>RequirePMStaff</c>); writes are admin-only (<c>RequirePMAdmin</c>) — the server is
/// the boundary. Thin lambdas: bind → dispatch via <see cref="ISender"/> → <c>TypedResults</c>. No user
/// management (P48); no delete (M2). Validation runs in the CQRS pipeline (the single validation home).
/// </summary>
public sealed class SettingsEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");

        group.MapGet("/org",
                async (ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new GetOrgSettings(), ct)))
            .RequireAuthorization("RequirePMStaff")
            .Produces<OrgSettingsResponse>();

        group.MapPut("/org",
                async (UpdateOrgSettings body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body, ct)))
            .RequireAuthorization("RequirePMAdmin")
            .Produces<OrgSettingsResponse>();

        group.MapGet("/banks",
                async (bool? activeOnly, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new ListBankAccounts(activeOnly ?? false), ct)))
            .RequireAuthorization("RequirePMStaff")
            .Produces<IReadOnlyList<BankAccountResponse>>();

        group.MapGet("/banks/{id:guid}",
                async Task<Results<Ok<BankAccountResponse>, NotFound>> (Guid id, ISender sender, CancellationToken ct) =>
                    await sender.Query(new GetBankAccount(id), ct) is { } bank
                        ? TypedResults.Ok(bank)
                        : TypedResults.NotFound())
            .RequireAuthorization("RequirePMStaff");

        group.MapPost("/banks",
                async (CreateBankAccount body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body, ct)))
            .RequireAuthorization("RequirePMAdmin")
            .Produces<BankAccountResponse>();

        group.MapPut("/banks/{id:guid}",
                async Task<Results<Ok<BankAccountResponse>, NotFound>> (
                    Guid id, UpdateBankAccountRequest body, ISender sender, CancellationToken ct) =>
                    await sender.Send(new UpdateBankAccount(id, body.Name, body.Institution, body.Mask), ct) is { } bank
                        ? TypedResults.Ok(bank)
                        : TypedResults.NotFound())
            .RequireAuthorization("RequirePMAdmin");

        group.MapPut("/banks/{id:guid}/active",
                async Task<Results<Ok<BankAccountResponse>, NotFound, ProblemHttpResult>> (
                    Guid id, SetBankActiveRequest body, ISender sender, CancellationToken ct) =>
                {
                    var result = await sender.Send(new SetBankAccountActive(id, body.IsActive), ct);
                    return result.Outcome switch
                    {
                        SetActiveOutcome.Updated => TypedResults.Ok(result.Bank!),
                        SetActiveOutcome.NotFound => TypedResults.NotFound(),
                        _ => TypedResults.Problem(
                            title: "Bank account has uncleared items",
                            detail: "Clear or reconcile outstanding items before deactivating.",
                            statusCode: StatusCodes.Status409Conflict,
                            extensions: new Dictionary<string, object?> { ["code"] = "bank_account_has_uncleared" }),
                    };
                })
            .RequireAuthorization("RequirePMAdmin")
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}

/// <summary>Body for <c>PUT /banks/{id}</c> — the id comes from the route.</summary>
public sealed record UpdateBankAccountRequest(string Name, string? Institution, string? Mask);

/// <summary>Body for <c>PUT /banks/{id}/active</c> — the id comes from the route.</summary>
public sealed record SetBankActiveRequest(bool IsActive);
