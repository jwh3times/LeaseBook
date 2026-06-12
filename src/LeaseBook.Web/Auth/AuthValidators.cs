using FluentValidation;

namespace LeaseBook.Web.Auth;

// One validator per request DTO (P23). Auth bypasses the CQRS dispatcher, so these run in the
// ValidationEndpointFilter — that filter is their single execution home.

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class MfaRequestValidator : AbstractValidator<MfaRequest>
{
    public MfaRequestValidator()
    {
        RuleFor(x => x.MfaToken).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().Matches(@"^\d{6}$").WithMessage("Code must be six digits.");
    }
}

public sealed class ConfirmMfaRequestValidator : AbstractValidator<ConfirmMfaRequest>
{
    public ConfirmMfaRequestValidator() =>
        RuleFor(x => x.Code).NotEmpty().Matches(@"^\d{6}$").WithMessage("Code must be six digits.");
}
