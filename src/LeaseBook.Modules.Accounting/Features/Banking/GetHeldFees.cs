using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Banking;

/// <summary>
/// The PM's management fees held in one bank (#743): today's balance, and the lowest balance on or after
/// <paramref name="From"/> — the held-fees floor a bank fee, a transfer and a sweep dated then are paid
/// from (ADR-014 addendum). The register's sweep prefills its amount with the floor, so the screen offers
/// exactly what the posting guard will accept. A floor below zero is a breach I11 reports; it is returned
/// as it is, and the screen shows it as nothing to sweep.
/// </summary>
public sealed record GetHeldFees(Guid BankAccountId, DateOnly From) : IQuery<HeldFeesView>;

public sealed record HeldFeesView(Guid BankAccountId, DateOnly From, decimal Held, decimal Floor);

internal sealed class GetHeldFeesHandler(DbContext db) : IQueryHandler<GetHeldFees, HeldFeesView>
{
    public async Task<HeldFeesView> Handle(GetHeldFees query, CancellationToken ct)
    {
        var balances = new BalanceReader(db);
        var held = await balances.HeldFeesAsync(query.BankAccountId, ct);
        var floor = await balances.HeldFeesFloorAsync(query.BankAccountId, query.From, ct);
        return new HeldFeesView(query.BankAccountId, query.From, held, floor);
    }
}
