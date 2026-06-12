namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// A bank account to provision a chart-of-accounts entry for. The <see cref="BankAccountId"/> is a
/// bare uuid in M1 (P26) — the Banking module's account table that it will FK to does not exist until
/// M4 — and is the dimension every funds-moving journal line carries (P36), which is what makes the
/// trust equation computable per bank.
/// </summary>
/// <param name="BankAccountId">Stable id of the bank account (reused by the M4 directory row).</param>
/// <param name="Name">Display name for the provisioned account (e.g. "Operating Trust").</param>
/// <param name="Purpose">Fixes the provisioned account class (§C.2).</param>
public sealed record BankAccountSpec(Guid BankAccountId, string Name, BankPurpose Purpose);
