using LeaseBook.Modules.Accounting.Domain;

namespace LeaseBook.Modules.Accounting.Periods;

/// <summary>
/// The accounting-period lifecycle the posting engine checks against (P32). Module-internal: the
/// posting service consumes <see cref="GetOpenPeriodAsync"/>; <see cref="CloseAsync"/> is exercised by
/// tests now and wired to M4 reconciliation finalize later. No API exposure in M1.
/// </summary>
internal interface IAccountingPeriods
{
    /// <summary>
    /// The period for <paramref name="date"/>'s month, created lazily as <see cref="PeriodStatus.Open"/>
    /// if it does not exist. Returns the existing period whatever its status — the caller checks
    /// open/closed (the posting service rejects closed periods).
    /// </summary>
    Task<AccountingPeriod> GetOpenPeriodAsync(DateOnly date, CancellationToken ct);

    /// <summary>
    /// Closes the (year, month) period (get-or-create then open→closed, stamping <c>closed_at</c>).
    /// Closing an already-closed period is a no-op that returns it. No reopen in M1.
    /// </summary>
    Task<AccountingPeriod> CloseAsync(int year, int month, CancellationToken ct);
}
