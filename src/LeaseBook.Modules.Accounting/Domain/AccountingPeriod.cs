using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// A monthly accounting period for one org, unique on <c>(org_id, year, month)</c> (P32). Created
/// lazily as <see cref="PeriodStatus.Open"/> on the first posting into its month; the engine rejects
/// postings into a <see cref="PeriodStatus.Closed"/> period. No reopen in M1.
/// </summary>
public sealed class AccountingPeriod : IOrgScoped
{
    private AccountingPeriod()
    {
        // EF + the factory below.
    }

    private AccountingPeriod(int year, int month)
    {
        Id = UuidV7.NewId();
        Year = year;
        Month = month;
        Status = PeriodStatus.Open;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; set; }

    public int Year { get; private set; }

    public int Month { get; private set; }

    public PeriodStatus Status { get; private set; }

    public DateTime? ClosedAt { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Module-internal factory — opened lazily by the period service (WP-03).</summary>
    internal static AccountingPeriod Open(int year, int month) => new(year, month);

    /// <summary>Idempotent close: flips an open period to closed; a no-op if already closed.</summary>
    internal void Close(DateTime closedAt)
    {
        if (Status == PeriodStatus.Closed)
        {
            return;
        }

        Status = PeriodStatus.Closed;
        ClosedAt = closedAt;
    }
}
