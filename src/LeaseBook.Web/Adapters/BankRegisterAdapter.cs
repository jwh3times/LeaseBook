using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Banking.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / P68) for Banking's <see cref="IBankRegister"/> port. Delegates to Accounting's
/// <see cref="GetBankRegister"/> read model via <see cref="ISender"/>, keeps only the uncleared rows, and
/// projects them to the matcher's <see cref="RegisterCandidate"/> (signed amount: deposit +, withdrawal −).
/// Pages through the window so the match set is complete. Banking never touches the journal directly.
/// </summary>
internal sealed class BankRegisterAdapter(ISender sender) : IBankRegister
{
    private const int PageSize = 200;

    public async Task<IReadOnlyList<RegisterCandidate>> GetUnclearedAsync(
        Guid bankAccountId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var candidates = new List<RegisterCandidate>();
        var page = 1;

        while (true)
        {
            var response = await sender.Query(
                new GetBankRegister(bankAccountId, null, RegisterTypeFilter.All, from, to, null, page, PageSize), ct);

            foreach (var row in response.Rows)
            {
                if (row.Status != BankLineStatus.Uncleared)
                {
                    continue;
                }

                var amount = row.Deposit ?? (row.Withdrawal is { } withdrawal ? -withdrawal : 0m);
                candidates.Add(new RegisterCandidate(
                    row.JournalLineId, row.Date, amount, row.Description ?? string.Empty));
            }

            if (page * PageSize >= response.Total)
            {
                break;
            }

            page++;
        }

        return candidates;
    }
}
