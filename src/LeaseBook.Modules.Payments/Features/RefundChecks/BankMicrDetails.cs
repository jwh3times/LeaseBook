using System.Text.RegularExpressions;
using FluentValidation;
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
    decimal MicrOffsetXPoints, decimal MicrOffsetYPoints) : ICommand<BankMicrDetailsView>;

public sealed class SaveBankMicrDetailsValidator : AbstractValidator<SaveBankMicrDetails>
{
    public SaveBankMicrDetailsValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.StockKind).Must(k => CheckStockKinds.All.Contains(k))
            .WithMessage("Stock kind must be 'preprinted' or 'blank'.");
        RuleFor(x => x.RoutingNumber!).Must(BankMicr.IsValidRoutingNumber)
            .When(x => x.RoutingNumber is not null)
            .WithMessage("The routing number must be nine digits with a valid check digit.");
        RuleFor(x => x.OnUsAccountNumber!).Must(BankMicr.IsValidOnUsField)
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
            throw new ValidationException("Blank check stock needs the routing number and the On-Us field.");
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
public static partial class BankMicr
{
    /// <summary>How far the MICR line may be moved from its nominal position: a quarter inch either way.</summary>
    public const decimal OffsetLimitPoints = 18m;

    /// <summary>
    /// An ABA routing number: nine digits whose weighted sum (3, 7, 1 repeating) is a multiple of ten.
    /// All zeros passes the arithmetic but names no institution, so it is refused.
    /// </summary>
    public static bool IsValidRoutingNumber(string value)
    {
        if (value.Length != 9 || !value.All(char.IsAsciiDigit) || value == "000000000")
        {
            return false;
        }

        int[] weights = [3, 7, 1, 3, 7, 1, 3, 7, 1];
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (value[i] - '0') * weights[i];
        }

        return sum % 10 == 0;
    }

    /// <summary>
    /// The On-Us field as the bank's specification sheet prints it, left to right: digits, <c>-</c> for the
    /// dash symbol, a space for an empty position and <c>U</c> for the On-Us symbol; at most 18 characters
    /// (positions 14–31) and at least four digits.
    /// </summary>
    public static bool IsValidOnUsField(string value) =>
        OnUsField().IsMatch(value) && value.Count(char.IsAsciiDigit) >= 4;

    internal static BankMicrDetailsView View(Guid bankAccountId, BankMicrProfile? row) => new(
        bankAccountId,
        row?.StockKind ?? CheckStockKinds.PrePrinted,
        LastFourDigits(row?.RoutingNumber),
        LastFourDigits(row?.OnUsAccountNumber),
        row?.MicrOffsetXPoints ?? 0m,
        row?.MicrOffsetYPoints ?? 0m);

    private static string? LastFourDigits(string? value) =>
        value is null ? null : new string(value.Where(char.IsAsciiDigit).TakeLast(4).ToArray());

    [GeneratedRegex("^[0-9U\\- ]{1,18}$")]
    private static partial Regex OnUsField();
}
