using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Onboarding.Verification;

/// <summary>
/// WP-4 verification + sign-off endpoints (M7).
///
/// <list type="bullet">
/// <item>
///   <c>POST /api/onboarding/verification</c> — submit operator AppFolio closing figures, compute
///   the line-by-line variance report, persist a <c>migration_verifications</c> row, and return the
///   report. Re-verification appends a new row (never upsert).
/// </item>
/// <item>
///   <c>POST /api/onboarding/verification/{id}/signoff</c> — if <c>IsTied == false</c> → HTTP 409
///   <c>not_tied</c> with NO side effect (no row written, no audit event). If tied → inserts the
///   signed verification row and an explicit <c>migration-signed-off</c> audit event → 200.
/// </item>
/// </list>
/// </summary>
public sealed class VerificationEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/onboarding/verification")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Onboarding");

        // POST /api/onboarding/verification
        // Body: { cutoverDate, ownerEquityTotal, depositLiabilityTotal, bankBookBalances: [...] }
        // Returns: VerificationReport (200)
        group.MapPost("/",
                async (VerificationRequestDto body, VerificationService service, HttpContext httpContext,
                    CancellationToken ct) =>
                {
                    if (!DateOnly.TryParse(body.CutoverDate, out var cutoverDate))
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: "invalid_cutover_date",
                            detail: "The cutover date must be a valid date in YYYY-MM-DD format.",
                            status: StatusCodes.Status400BadRequest);
                    }

                    var banks = (body.BankBookBalances ?? [])
                        .Select(b => new OperatorBankBalance(b.BankAccountId, b.ExpectedBook, b.AccountCode))
                        .ToList();

                    var request = new VerificationRequest(
                        cutoverDate,
                        body.OwnerEquityTotal,
                        body.DepositLiabilityTotal,
                        banks);

                    var report = await service.VerifyAsync(request, ct);
                    return TypedResults.Ok(report);
                })
            .Produces<VerificationReport>()
            .Produces(StatusCodes.Status400BadRequest);

        // POST /api/onboarding/verification/{id}/signoff
        // Returns: SignoffResult (200) or 409 not_tied (if IsTied == false)
        group.MapPost("/{id:guid}/signoff",
                async (Guid id, VerificationService service, HttpContext httpContext, CancellationToken ct) =>
                {
                    try
                    {
                        var result = await service.SignOffAsync(id, ct);
                        return Results.Ok(result);
                    }
                    catch (MigrationNotTiedException ex)
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: "not_tied",
                            detail: ex.Message,
                            status: StatusCodes.Status409Conflict,
                            extensions: new Dictionary<string, object?>
                            {
                                ["verificationId"] = ex.VerificationId,
                                ["varianceTotal"] = ex.VarianceTotal,
                                ["clearingCash"] = ex.ClearingCash,
                                ["clearingAccrual"] = ex.ClearingAccrual,
                            });
                    }
                    catch (KeyNotFoundException)
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: "verification_not_found",
                            detail: "That verification was not found. Re-run verification and try again.",
                            status: StatusCodes.Status404NotFound);
                    }
                })
            .Produces<SignoffResult>()
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status404NotFound);
    }
}

// ── Request DTO (thin binding layer; domain types stay in VerificationService) ─────────────────────

/// <summary>JSON body for <c>POST /api/onboarding/verification</c>.</summary>
public sealed record VerificationRequestDto(
    string? CutoverDate,
    decimal OwnerEquityTotal,
    decimal DepositLiabilityTotal,
    IReadOnlyList<BankBalanceDto>? BankBookBalances);

/// <summary>One bank account's expected book balance in the operator's closing figures.</summary>
public sealed record BankBalanceDto(
    Guid BankAccountId,
    decimal ExpectedBook,
    string? AccountCode);
