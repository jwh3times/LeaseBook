using FluentValidation;

namespace LeaseBook.SharedKernel.Cqrs.Decorators;

/// <summary>
/// The single validation execution point for commands (§C.8 / P23). Runs every registered
/// FluentValidation validator for the command; on any failure throws <see cref="ValidationException"/>
/// carrying the full failure set, short-circuiting before the handler runs.
/// </summary>
public sealed class ValidationCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> Handle(TCommand command, CancellationToken ct)
    {
        var active = validators as IValidator<TCommand>[] ?? validators.ToArray();
        if (active.Length > 0)
        {
            var context = new ValidationContext<TCommand>(command);
            var results = await Task.WhenAll(active.Select(v => v.ValidateAsync(context, ct)));
            var failures = results.SelectMany(r => r.Errors).Where(f => f is not null).ToArray();
            if (failures.Length > 0)
            {
                throw new ValidationException(failures);
            }
        }

        return await inner.Handle(command, ct);
    }
}
