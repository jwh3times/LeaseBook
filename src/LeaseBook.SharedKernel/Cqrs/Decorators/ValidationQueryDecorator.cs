using FluentValidation;

namespace LeaseBook.SharedKernel.Cqrs.Decorators;

/// <summary>Query counterpart of <see cref="ValidationCommandDecorator{TCommand,TResult}"/>.</summary>
public sealed class ValidationQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IEnumerable<IValidator<TQuery>> validators) : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    public async Task<TResult> Handle(TQuery query, CancellationToken ct)
    {
        var active = validators as IValidator<TQuery>[] ?? validators.ToArray();
        if (active.Length > 0)
        {
            var context = new ValidationContext<TQuery>(query);
            var results = await Task.WhenAll(active.Select(v => v.ValidateAsync(context, ct)));
            var failures = results.SelectMany(r => r.Errors).Where(f => f is not null).ToArray();
            if (failures.Length > 0)
            {
                throw new ValidationException(failures);
            }
        }

        return await inner.Handle(query, ct);
    }
}
