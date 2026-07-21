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
///
/// <c>POST /api/onboarding/import-balances/{kind}/supersede</c> — the WP-7 pre-sign-off corrected
/// re-import (<see cref="BalanceImportService.SupersedeAsync"/>): same request/response shape, but
/// diffs the corrected CSV against the live opening positions instead of posting fresh. The three
/// §2 guards surface as a 409 problem via <see cref="SupersedeConflictException"/>. Both routes
/// share <see cref="ValidateImportRequest"/> for the request-shape checks so they cannot drift.
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
                    HttpContext httpContext,
                    CancellationToken ct) =>
                {
                    var invalid = ValidateImportRequest(kind, body, httpContext, out var balanceKind, out var cutoverDate);
                    if (invalid is not null) return invalid;

                    var csvBytes = System.Text.Encoding.UTF8.GetBytes(body.CsvContent!);
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

        group.MapPost("/import-balances/{kind}/supersede",
                async (
                    string kind,
                    BalanceImportRequest body,
                    BalanceImportService service,
                    HttpContext httpContext,
                    CancellationToken ct) =>
                {
                    var invalid = ValidateImportRequest(kind, body, httpContext, out var balanceKind, out var cutoverDate);
                    if (invalid is not null) return invalid;

                    var csvBytes = System.Text.Encoding.UTF8.GetBytes(body.CsvContent!);
                    await using var csvStream = new MemoryStream(csvBytes);

                    try
                    {
                        var result = await service.SupersedeAsync(
                            balanceKind, "appfolio-default", body.Filename ?? $"{kind}.csv",
                            cutoverDate, csvStream, ct);
                        return TypedResults.Ok(result);
                    }
                    catch (SupersedeConflictException ex)
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: ex.Code,
                            detail: ex.Message,
                            status: StatusCodes.Status409Conflict);
                    }
                })
            .Produces<ImportBatchResult>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict);
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

    /// <summary>
    /// The request-shape validation ladder shared verbatim by the import and supersede routes (kind →
    /// mapping profile → cutover date → csv presence), so the two routes cannot silently drift apart.
    /// Returns null when every check passes; <paramref name="balanceKind"/> and
    /// <paramref name="cutoverDate"/> are only meaningful when the return value is null.
    /// </summary>
    private static IResult? ValidateImportRequest(
        string kind,
        BalanceImportRequest body,
        HttpContext httpContext,
        out EntityKind balanceKind,
        out DateOnly cutoverDate)
    {
        balanceKind = default;
        cutoverDate = default;

        if (!TryParseBalanceKind(kind, out balanceKind))
            return ProblemResults.Problem(
                httpContext,
                code: "invalid_balance_kind",
                detail: "That is not a balance type this import supports.",
                status: StatusCodes.Status400BadRequest);

        // Only the documented appfolio-default profile exists today.
        var requested = body.MappingProfile;
        if (!string.IsNullOrWhiteSpace(requested) && requested != "appfolio-default")
            return ProblemResults.Problem(
                httpContext,
                code: "unknown_mapping_profile",
                detail: "That column-mapping profile is not available.",
                status: StatusCodes.Status400BadRequest);

        if (!DateOnly.TryParse(body.CutoverDate, out cutoverDate))
            return ProblemResults.Problem(
                httpContext,
                code: "invalid_cutover_date",
                detail: "The cutover date must be a valid date in YYYY-MM-DD format.",
                status: StatusCodes.Status400BadRequest);

        if (string.IsNullOrWhiteSpace(body.CsvContent))
            return ProblemResults.Problem(
                httpContext,
                code: "empty_csv",
                detail: "The uploaded file is empty.",
                status: StatusCodes.Status400BadRequest);

        return null;
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
