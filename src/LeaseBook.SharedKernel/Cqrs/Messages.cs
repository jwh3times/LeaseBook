namespace LeaseBook.SharedKernel.Cqrs;

/// <summary>
/// Marker for a command — a state-changing request producing <typeparamref name="TResult"/>.
/// Commands mutate state only through domain services (§C.8).
/// </summary>
public interface ICommand<TResult>
{
}

/// <summary>
/// Marker for a query — a read request producing <typeparamref name="TResult"/>.
/// Queries read projections / SQL directly and never load aggregates (§C.8).
/// </summary>
public interface IQuery<TResult>
{
}

/// <summary>Handles exactly one command type.</summary>
public interface ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> Handle(TCommand command, CancellationToken ct);
}

/// <summary>Handles exactly one query type.</summary>
public interface IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> Handle(TQuery query, CancellationToken ct);
}

/// <summary>
/// The single dispatch entry point. Resolves the handler for a message and runs it through the
/// decorator pipeline (telemetry → validation → handler, P24). Hand-rolled; no MediatR (P21).
/// </summary>
public interface ISender
{
    Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default);
    Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default);
}
