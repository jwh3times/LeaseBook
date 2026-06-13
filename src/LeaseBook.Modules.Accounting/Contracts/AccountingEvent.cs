namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// Base of every postable business event (§C.3). An event is the domain-language description of
/// something that happened (rent charged, payment received, owner disbursed); the matching posting
/// template (WP-05) translates it into a balanced <see cref="PostEntryRequest"/>. Posted through the
/// single <see cref="IAccountingEvents"/> surface so the seeder, the M3 composer, and M6 fee runs all
/// share one entry point.
/// </summary>
public abstract record AccountingEvent;
