using FluentValidation;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// Runs the registered FluentValidation validator for a request DTO on non-CQRS endpoints (auth),
/// returning a 400 ProblemDetails with the <c>errors</c> dictionary on failure. CQRS slices validate
/// in the dispatcher's ValidationDecorator instead (§C.8) — never both for the same message.
/// </summary>
public sealed class ValidationEndpointFilter<T>(IValidator<T> validator) : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var model = context.Arguments.OfType<T>().FirstOrDefault();
        if (model is not null)
        {
            var result = await validator.ValidateAsync(model, context.HttpContext.RequestAborted);
            if (!result.IsValid)
            {
                var errors = result.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
                return Results.ValidationProblem(errors);
            }
        }

        return await next(context);
    }
}
