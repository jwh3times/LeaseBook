namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// The six account classes of the trust-accounting model (§C.1 / CLAUDE.md). Class — not a reporting
/// filter — is what isolates PM income from owner income at the data-model level: no owner-facing
/// query selects <see cref="PmIncome"/>. Stored as snake_case text (a DB CHECK backs the set), and
/// <b>denormalized onto every journal line</b> (P25) so Postgres CHECK constraints can reason about
/// it without joining back to <c>accounts</c>.
/// </summary>
public enum AccountClass
{
    /// <summary>A trust bank account — fiduciary funds held for owners/tenants. Carries the trust equation.</summary>
    TrustBank,

    /// <summary>Owner equity (the owner's money held in trust). Subledgered by owner/property dims on lines.</summary>
    OwnerEquity,

    /// <summary>Amounts tenants owe (rent, fees). Accrual-side; never represents cash in a bank.</summary>
    TenantReceivable,

    /// <summary>Security deposits and prepayments — liabilities until applied (P35 splits this into two accounts).</summary>
    DepositLiability,

    /// <summary>The PM's earned management income. Structurally unreachable by owner statements (CLAUDE.md).</summary>
    PmIncome,

    /// <summary>The PM's own operating bank account — receives swept fees; outside the trust equation.</summary>
    PmOperatingBank,
}
