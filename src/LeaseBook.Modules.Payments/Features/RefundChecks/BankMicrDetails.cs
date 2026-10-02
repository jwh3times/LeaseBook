using FluentValidation;
using FluentValidation.Results;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features.RefundChecks;

/// <summary>
/// A bank account's MICR details as any read shows them (#474): the stock kind, the MICR line offsets,
/// and the last four digits of the routing number and of the On-Us field — never the full numbers.
/// Null last-four means the number has not been saved.
/// </summary>
public sealed record BankMicrDetailsView(
    Guid BankAccountId, string StockKind, string? RoutingNumberLast4, string? OnUsAccountNumberLast4,
    decimal MicrOffsetXPoints, decimal MicrOffsetYPoints);

public sealed record GetBankMicrDetails(Guid BankAccountId) : IQuery<BankMicrDetailsView>;

internal sealed class GetBankMicrDetailsHandler(DbContext db) : IQueryHandler<GetBankMicrDetails, BankMicrDetailsView>
{
    public async Task<BankMicrDetailsView> Handle(GetBankMicrDetails q, CancellationToken ct)
    {
        var row = await db.Set<BankMicrProfile>().AsNoTracking().FirstOrDefaultAsync(x => x.BankAccountId == q.BankAccountId, ct);
        return BankMicr.View(q.BankAccountId, row);
    }
}

/// <summary>
/// Saves a bank account's MICR details (#474). The numbers are write-only: a null number keeps the saved
/// one, because no read ever hands the full value back for a form to resend. Blank stock needs both
/// numbers, saved or supplied, since its checks print them.
/// </summary>
public sealed record SaveBankMicrDetails(
    Guid BankAccountId, string StockKind, string? RoutingNumber, string? OnUsAccountNumber,
    decimal MicrOffsetXPoints, decimal MicrOffsetYPoints) : ICommand<BankMicrDetailsView>
{
    /// <summary>A record prints every member; this one never prints the numbers.</summary>
    public override string ToString() =>
        $"{nameof(SaveBankMicrDetails)} {{ BankAccountId = {BankAccountId}, StockKind = {StockKind}, " +
        $"RoutingNumber = {BankMicr.Redacted(RoutingNumber)}, OnUsAccountNumber = {BankMicr.Redacted(OnUsAccountNumber)}, " +
        $"MicrOffsetXPoints = {MicrOffsetXPoints}, MicrOffsetYPoints = {MicrOffsetYPoints} }}";
}

public sealed class SaveBankMicrDetailsValidator : AbstractValidator<SaveBankMicrDetails>
{
    public SaveBankMicrDetailsValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.StockKind).Must(k => CheckStockKinds.All.Contains(k))
            .WithMessage("Stock kind must be 'preprinted' or 'blank'.");
        RuleFor(x => x.RoutingNumber!).Must(MicrLine.IsValidRoutingNumber)
            .When(x => x.RoutingNumber is not null)
            .WithMessage("The routing number must be nine digits with a valid check digit.");
        RuleFor(x => x.OnUsAccountNumber!).Must(MicrLine.IsValidOnUsField)
            .When(x => x.OnUsAccountNumber is not null)
            .WithMessage("The On-Us field must be at most 18 digits, dashes, spaces or 'U' symbols, with at least four digits.");
        RuleFor(x => x.MicrOffsetXPoints).InclusiveBetween(-BankMicr.OffsetLimitPoints, BankMicr.OffsetLimitPoints)
            .Must(v => decimal.Round(v, 2) == v);
        RuleFor(x => x.MicrOffsetYPoints).InclusiveBetween(-BankMicr.OffsetLimitPoints, BankMicr.OffsetLimitPoints)
            .Must(v => decimal.Round(v, 2) == v);
    }
}

internal sealed class SaveBankMicrDetailsHandler(DbContext db, TimeProvider clock)
    : ICommandHandler<SaveBankMicrDetails, BankMicrDetailsView>
{
    public async Task<BankMicrDetailsView> Handle(SaveBankMicrDetails c, CancellationToken ct)
    {
        var row = await db.Set<BankMicrProfile>().FirstOrDefaultAsync(x => x.BankAccountId == c.BankAccountId, ct);
        if (row is null)
        {
            row = new BankMicrProfile { Id = UuidV7.NewId(), BankAccountId = c.BankAccountId };
            db.Add(row);
        }

        var routing = c.RoutingNumber ?? row.RoutingNumber;
        var onUs = c.OnUsAccountNumber ?? row.OnUsAccountNumber;
        if (c.StockKind == CheckStockKinds.Blank && (routing is null || onUs is null))
        {
            // A field failure, so the 400 carries the reason the form shows (a bare message does not).
            throw new ValidationException(
            [
                new ValidationFailure(nameof(SaveBankMicrDetails.StockKind),
                    "Blank check stock needs the routing number and the On-Us field."),
            ]);
        }

        row.StockKind = c.StockKind;
        row.RoutingNumber = routing;
        row.OnUsAccountNumber = onUs;
        row.MicrOffsetXPoints = c.MicrOffsetXPoints;
        row.MicrOffsetYPoints = c.MicrOffsetYPoints;
        row.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return BankMicr.View(c.BankAccountId, row);
    }
}

/// <summary>Rules for a bank account's MICR numbers (#474; evidence in docs/research/micr-e13b-refund-checks.md).</summary>
public static class BankMicr
{
    /// <summary>How far the MICR line may be moved from its nominal position: a quarter inch either way.</summary>
    public const decimal OffsetLimitPoints = 18m;

    internal static BankMicrDetailsView View(Guid bankAccountId, BankMicrProfile? row) => new(
        bankAccountId,
        row?.StockKind ?? CheckStockKinds.PrePrinted,
        LastFourDigits(row?.RoutingNumber),
        LastFourDigits(row?.OnUsAccountNumber),
        row?.MicrOffsetXPoints ?? 0m,
        row?.MicrOffsetYPoints ?? 0m);

    /// <summary>How a number appears in any printed form of a request: whether it was sent, never what it is.</summary>
    public static string Redacted(string? value) => value is null ? "null" : "[redacted]";

    /// <summary>
    /// Refuses to print a check or an alignment page on an account set to blank stock until the MICR line
    /// can be printed (#474); see <see cref="RefundCheckConflictException.BlankStockUnsupported"/>.
    /// </summary>
    public static void EnsurePrintable(BankMicrDetailsView details)
    {
        if (details.StockKind == CheckStockKinds.Blank)
        {
            throw RefundCheckConflictException.BlankStockUnsupported();
        }
    }

    private static string? LastFourDigits(string? value) =>
        value is null ? null : new string(value.Where(char.IsAsciiDigit).TakeLast(4).ToArray());
}
