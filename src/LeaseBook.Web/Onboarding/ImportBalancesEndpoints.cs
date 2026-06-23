using LeaseBook.Migrator.Model;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Onboarding;

/// <summary>
/// Balance-import endpoints for the M7 onboarding wizard (WP-3 Task 3.2).
///
/// <c>POST /api/onboarding/import-balances/{kind}</c> — uploads a CSV (as JSON body with
/// <c>csvContent</c> string field, mirroring the entity-import pattern), parses it against
/// the AppFolio default profile, resolves external ids to LeaseBook ids, and posts one opening
/// position per row via <see cref="IBalanceForward.PostOpeningPositionAsync"/>. Returns an
/// <see cref="ImportBatchResult"/> with per-row error detail. Non-tying imports succeed (clearing
/// accumulates the residual; WP-4 verification blocks go-live, not this endpoint).
/// </summary>
public sealed class ImportBalancesEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/onboarding")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Onboarding");

        group.MapPost("/import-balances/{kind}",
                async (
                    string kind,
                    BalanceImportRequest body,
                    BalanceImportService service,
                    CancellationToken ct) =>
                {
                    if (!TryParseBalanceKind(kind, out var balanceKind))
                        return Results.Problem(
                            title: "Invalid balance kind",
                            detail: $"'{kind}' is not a valid balance kind for import. " +
                                    "Use one of: owner_balances, deposit_liabilities, bank_balances, tenant_receivables.",
                            statusCode: StatusCodes.Status400BadRequest);

                    // Only the documented appfolio-default profile exists today.
                    var requested = body.MappingProfile;
                    if (!string.IsNullOrWhiteSpace(requested) && requested != "appfolio-default")
                        return Results.Problem(
                            title: "Unknown mapping profile",
                            detail: $"unknown_mapping_profile: '{requested}'. " +
                                    "The only supported profile is 'appfolio-default'.",
                            statusCode: StatusCodes.Status400BadRequest);

                    if (!DateOnly.TryParse(body.CutoverDate, out var cutoverDate))
                        return Results.Problem(
                            title: "Invalid cutover date",
                            detail: $"cutover_date '{body.CutoverDate}' is not a valid ISO date (yyyy-MM-dd).",
                            statusCode: StatusCodes.Status400BadRequest);

                    var csvBytes = System.Text.Encoding.UTF8.GetBytes(body.CsvContent ?? string.Empty);
                    await using var csvStream = new MemoryStream(csvBytes);

                    var result = await service.ImportAsync(
                        balanceKind,
                        "appfolio-default",
                        body.Filename ?? $"{kind}.csv",
                        cutoverDate,
                        csvStream,
                        ct);

                    return TypedResults.Ok(result);
                })
            .Produces<ImportBatchResult>()
            .Produces(StatusCodes.Status400BadRequest);
    }

    // The route uses snake_case kind tokens (e.g. "owner_balances"); normalise to PascalCase
    // (strip underscores) before the idiomatic Enum.TryParse + IsDefined check.
    private static bool TryParseBalanceKind(string raw, out EntityKind kind)
    {
        var normalised = raw.Replace("_", string.Empty);
        return Enum.TryParse(normalised, ignoreCase: true, out kind)
               && Enum.IsDefined(kind)
               && kind is EntityKind.OwnerBalances or EntityKind.DepositLiabilities
                   or EntityKind.BankBalances or EntityKind.TenantReceivables;
    }
}

/// <summary>Request body for <c>POST /api/onboarding/import-balances/{kind}</c>.</summary>
public sealed record BalanceImportRequest(
    /// <summary>CSV file content as UTF-8 text.</summary>
    string? CsvContent,
    /// <summary>Cutover date in ISO format yyyy-MM-dd (required).</summary>
    string? CutoverDate,
    /// <summary>Original filename (for display / audit).</summary>
    string? Filename,
    /// <summary>Optional mapping profile override; defaults to <c>appfolio-default</c>.</summary>
    string? MappingProfile);
