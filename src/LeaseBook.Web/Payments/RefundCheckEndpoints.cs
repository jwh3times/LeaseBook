using System.Text.Json.Serialization;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LeaseBook.Web.Payments;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IssueRefundCheckBody(
    Guid Key, Guid TenantId, string Source, decimal Amount, DateOnly Date, int CheckNumber,
    string PayeeName, string AddressLine1, string? AddressLine2, string City, string State, string PostalCode,
    string? Memo, string? InternalNote, RefundFundsBucket? Bucket = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoidRefundCheckBody(string Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CheckPrintSettingsBody(decimal OffsetXPoints, decimal OffsetYPoints);

/// <summary>A null number keeps the saved one (#474): reads never return the full values to resend.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BankMicrDetailsBody(
    string StockKind, string? RoutingNumber, string? OnUsAccountNumber, decimal MicrOffsetXPoints,
    decimal MicrOffsetYPoints);

/// <summary>
/// Staff refund-check surface (#473). Issue, list and void ride the ordinary organization transaction;
/// the PDF routes are POSTs because each records a print, and their responses are <c>no-store</c> —
/// a rendered check carries a payee and an amount.
/// </summary>
public sealed class RefundCheckEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/refund-checks").WithTags("Refund checks")
            .RequireAuthorization(AuthPolicies.RequirePMStaff);

        group.MapGet("/options", async (Guid tenantId, ISender sender, CancellationToken ct) =>
            TypedResults.Ok(await sender.Query(new GetRefundCheckOptions(tenantId), ct)));

        group.MapGet("", async (Guid? bankAccountId, Guid? tenantId, string? status, int? page, int? pageSize,
            ISender sender, CancellationToken ct) =>
            TypedResults.Ok(await sender.Query(new GetRefundChecks(
                bankAccountId, tenantId, Status: status, Page: page ?? 1, PageSize: pageSize ?? 50), ct)));

        group.MapPost("", async (IssueRefundCheckBody body, ISender sender, CancellationToken ct) =>
            TypedResults.Ok(await sender.Send(new IssueRefundCheck(
                body.Key, body.TenantId, body.Source, body.Amount, body.Date, body.CheckNumber, body.PayeeName,
                body.AddressLine1, body.AddressLine2, body.City, body.State, body.PostalCode, body.Memo,
                body.InternalNote, body.Bucket), ct)));

        group.MapPost("/{id:guid}/void", async Task<Results<Ok<RefundCheckView>, NotFound>> (
            Guid id, VoidRefundCheckBody body, ISender sender, CancellationToken ct) =>
            await sender.Send(new VoidIssuedRefundCheck(id, body.Reason), ct) is { } view
                ? TypedResults.Ok(view)
                : TypedResults.NotFound());

        group.MapPost("/{id:guid}/pdf", async Task<Results<FileContentHttpResult, NotFound>> (
            Guid id, ISender sender, HttpContext http, CancellationToken ct) =>
        {
            if (await sender.Send(new RecordRefundCheckPrint(id), ct) is not { } check)
            {
                return TypedResults.NotFound();
            }

            var settings = await sender.Query(new GetCheckPrintSettings(check.BankAccountId), ct);
            var pdf = RefundCheckPdf.Render(ToDocument(check), settings.OffsetXPoints, settings.OffsetYPoints);
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.File(pdf, "application/pdf", $"refund-check-{check.CheckNumber}.pdf");
        }).Produces(200, contentType: "application/pdf");

        group.MapGet("/print-settings/{bankAccountId:guid}", async (Guid bankAccountId, ISender sender, CancellationToken ct) =>
            TypedResults.Ok(await sender.Query(new GetCheckPrintSettings(bankAccountId), ct)));

        group.MapPut("/print-settings/{bankAccountId:guid}", async (
            Guid bankAccountId, CheckPrintSettingsBody body, ISender sender, CancellationToken ct) =>
            TypedResults.Ok(await sender.Send(
                new SaveCheckPrintSettings(bankAccountId, body.OffsetXPoints, body.OffsetYPoints), ct)));

        // #474: staff read the masked view (they print the checks); only an administrator changes the numbers.
        group.MapGet("/micr/{bankAccountId:guid}", async (Guid bankAccountId, ISender sender, CancellationToken ct) =>
            TypedResults.Ok(await sender.Query(new GetBankMicrDetails(bankAccountId), ct)));

        group.MapPut("/micr/{bankAccountId:guid}", async (
            Guid bankAccountId, BankMicrDetailsBody body, ISender sender, CancellationToken ct) =>
            TypedResults.Ok(await sender.Send(new SaveBankMicrDetails(
                bankAccountId, body.StockKind, body.RoutingNumber, body.OnUsAccountNumber, body.MicrOffsetXPoints,
                body.MicrOffsetYPoints), ct)))
            .RequireAuthorization(AuthPolicies.RequirePMAdmin);

        group.MapPost("/print-settings/{bankAccountId:guid}/alignment", async (
            Guid bankAccountId, ISender sender, TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            var settings = await sender.Query(new GetCheckPrintSettings(bankAccountId), ct);
            var sample = RefundCheckPdf.AlignmentSample(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.File(RefundCheckPdf.Render(sample, settings.OffsetXPoints, settings.OffsetYPoints),
                "application/pdf", "check-alignment-test.pdf");
        }).Produces(200, contentType: "application/pdf");
    }

    internal static RefundCheckDocument ToDocument(RefundCheckView check)
    {
        var address = new List<string> { check.PayeeName, check.AddressLine1 };
        if (!string.IsNullOrWhiteSpace(check.AddressLine2))
        {
            address.Add(check.AddressLine2);
        }

        address.Add($"{check.City}, {check.State} {check.PostalCode}");
        return new RefundCheckDocument(check.CheckNumber, check.IssueDate, check.PayeeName, check.Amount, address,
            check.Memo, check.Source == "deposit" ? "Security deposit refund" : "Prepaid credit refund");
    }
}
