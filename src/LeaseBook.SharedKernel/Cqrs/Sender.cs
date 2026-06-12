using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.SharedKernel.Cqrs;

/// <summary>
/// Resolves the (decorated) handler for a message from the current DI scope and invokes it.
/// The closed handler type is built from the message's runtime type, so the decorator chain
/// registered for that handler — telemetry(validation(handler)) — is what gets resolved.
/// </summary>
public sealed class Sender(IServiceProvider provider) : ISender
{
    public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult));
        var handler = provider.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"No command handler registered for '{command.GetType().FullName}' " +
                $"returning '{typeof(TResult).Name}'. Did you call AddLeaseBookCqrs with the right assembly?");
        return Invoke<TResult>(handler, command, ct);
    }

    public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
        var handler = provider.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"No query handler registered for '{query.GetType().FullName}' " +
                $"returning '{typeof(TResult).Name}'. Did you call AddLeaseBookCqrs with the right assembly?");
        return Invoke<TResult>(handler, query, ct);
    }

    // The handler's Handle(message, ct) is bound at runtime against the concrete message type.
    private static Task<TResult> Invoke<TResult>(object handler, object message, CancellationToken ct) =>
        ((dynamic)handler).Handle((dynamic)message, ct);
}
