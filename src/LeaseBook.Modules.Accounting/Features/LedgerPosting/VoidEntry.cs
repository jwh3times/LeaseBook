using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// Voids a posted entry → a linked reversal through <see cref="IReversalService"/> (append-only; the
/// original is never mutated, M3-E1). The reversal lands in the open period dated <see cref="AsOfDate"/>
/// (default today, P57). <see cref="SourceRef"/> is the P54 idempotency key, so a double-submitted void
/// maps to <c>duplicate_source_ref</c> (409) — and a re-void of an already-reversed entry to <c>already_reversed</c>.
/// </summary>
public sealed record VoidEntry(Guid EntryId, string Reason, DateOnly? AsOfDate, string SourceRef)
    : ICommand<PostResult>;

public sealed class VoidEntryValidator : AbstractValidator<VoidEntry>
{
    public VoidEntryValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty();
        RuleFor(x => x.SourceRef).NotEmpty();
    }
}

internal sealed class VoidEntryHandler(IReversalService reversal, TimeProvider clock)
    : ICommandHandler<VoidEntry, PostResult>
{
    public async Task<PostResult> Handle(VoidEntry command, CancellationToken ct)
    {
        var asOf = command.AsOfDate ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var id = await reversal.ReverseAsync(command.EntryId, command.Reason, asOf, command.SourceRef, ct);
        return new PostResult(id);
    }
}
