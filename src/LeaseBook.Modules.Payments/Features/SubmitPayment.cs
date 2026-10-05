using FluentValidation;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Payments.Features;

public sealed record SubmitPayment(FixtureBinding Binding, Guid TenantId, Guid UserId, Guid Key,
    decimal Amount, string Currency, string Method = PaymentMethods.Ach, decimal QuotedFee = 0m) : ICommand<PaymentView>;

/// <summary>What a tenant is shown before confirming: the method, the amount for their ledger, the fee and the total.</summary>
public sealed record PaymentQuoteView(string Method, decimal LedgerAmount, decimal Fee, decimal Charged);

/// <summary>Quotes the convenience fee for an amount and method under the organization's rule (ADR-053).</summary>
public sealed record GetPaymentQuote(FixtureBinding Binding, decimal Amount, string Method) : IQuery<PaymentQuoteView>;

public sealed class GetPaymentQuoteValidator : AbstractValidator<GetPaymentQuote>
{
    public GetPaymentQuoteValidator()
    {
        RuleFor(x => x.Amount).PaymentAmount();
        RuleFor(x => x.Method).PaymentMethod();
    }
}

internal sealed class GetPaymentQuoteHandler(PaymentEngine engine) : IQueryHandler<GetPaymentQuote, PaymentQuoteView>
{
    public async Task<PaymentQuoteView> Handle(GetPaymentQuote q, CancellationToken ct)
    {
        var quote = await engine.QuoteAsync(q.Binding, q.Amount, q.Method, ct);
        return new(q.Method, quote.LedgerAmount, quote.Fee, quote.Charged);
    }
}

internal static class PaymentRules
{
    public static IRuleBuilderOptions<T, decimal> PaymentAmount<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.InclusiveBetween(0.01m, 10000m).WithMessage("Enter an amount from $0.01 to $10,000.00.")
            .Must(x => decimal.Round(x, 2) == x).WithMessage("Enter an amount with at most two decimal places.");

    public static IRuleBuilderOptions<T, string> PaymentMethod<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(x => PaymentMethods.All.Contains(x)).WithMessage("Choose card or bank debit.");
}

public sealed class SubmitPaymentValidator : AbstractValidator<SubmitPayment>
{
    public SubmitPaymentValidator()
    {
        RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.TenantId).NotEmpty(); RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Currency).Equal("USD");
        RuleFor(x => x.Amount).PaymentAmount();
        RuleFor(x => x.Method).PaymentMethod();
        RuleFor(x => x.QuotedFee).GreaterThanOrEqualTo(0m).Must(x => decimal.Round(x, 2) == x)
            .WithMessage("The fee must be an amount in whole cents.");
    }
}

internal sealed class SubmitPaymentHandler(PaymentEngine engine) : ICommandHandler<SubmitPayment, PaymentView>
{
    public async Task<PaymentView> Handle(SubmitPayment c, CancellationToken ct) => PaymentView.From(
        await engine.SubmitAsync(c.Binding, c.TenantId, c.UserId, c.Key, c.Amount, c.Currency, c.Method, c.QuotedFee, ct));
}
