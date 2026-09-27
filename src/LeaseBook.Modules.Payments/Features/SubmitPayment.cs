using FluentValidation;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Payments.Features;

public sealed record SubmitPayment(FixtureBinding Binding, Guid TenantId, Guid UserId, Guid Key,
    decimal Amount, string Currency) : ICommand<PaymentView>;

public sealed class SubmitPaymentValidator : AbstractValidator<SubmitPayment>
{
    public SubmitPaymentValidator()
    {
        RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.TenantId).NotEmpty(); RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Currency).Equal("USD");
        RuleFor(x => x.Amount).InclusiveBetween(0.01m, 10000m).Must(x => decimal.Round(x, 2) == x)
            .WithMessage("Enter an amount with at most two decimal places.");
    }
}

internal sealed class SubmitPaymentHandler(PaymentEngine engine) : ICommandHandler<SubmitPayment, PaymentView>
{
    public async Task<PaymentView> Handle(SubmitPayment c, CancellationToken ct) => PaymentView.From(
        await engine.SubmitAsync(c.Binding, c.TenantId, c.UserId, c.Key, c.Amount, c.Currency, ct));
}
