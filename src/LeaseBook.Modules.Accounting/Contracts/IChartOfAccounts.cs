namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// Provisions an org's chart of accounts from the code template (§C.2). Idempotent by account
/// <c>code</c>, so it is safe to call on every startup/seed. Runs inside the <b>ambient org scope</b>
/// (the request middleware or <c>OrgScopedExecutor</c> establishes it) — there is no org parameter; the
/// stamping interceptor and RLS scope the writes.
/// </summary>
public interface IChartOfAccounts
{
    Task ProvisionAsync(IReadOnlyList<BankAccountSpec> banks, CancellationToken ct);
}
