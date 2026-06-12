namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// One detected breach of a correctness invariant (§C.7). <see cref="Invariant"/> is the id (e.g.
/// <c>I1</c>); <see cref="Detail"/> is a human-readable description used in the sweep report.
/// </summary>
public sealed record InvariantViolation(string Invariant, string Detail);

/// <summary>
/// The always-true correctness invariants over an org's live journal (§C.7), run by the
/// <c>check-invariants</c> CLI sweep and the test harness. Each returns a list of violations (empty =
/// clean), never just a bool, so a failure names what broke. I5 (basis convergence) and I6 (void nets
/// to zero) are relational/conditional and proven in the test harness, not swept here.
/// </summary>
public interface IInvariantChecks
{
    /// <summary>I1: every entry balances per basis. I2: the trust equation holds per trust bank.
    /// I3: no pm_income line carries an owner. I4: every held deposit/prepayment is ≥ 0.</summary>
    Task<IReadOnlyList<InvariantViolation>> CheckCoreAsync(CancellationToken ct);

    Task<IReadOnlyList<InvariantViolation>> CheckEntriesBalanceAsync(CancellationToken ct);

    Task<IReadOnlyList<InvariantViolation>> CheckTrustEquationAsync(CancellationToken ct);

    Task<IReadOnlyList<InvariantViolation>> CheckPmIncomeIsolationAsync(CancellationToken ct);

    Task<IReadOnlyList<InvariantViolation>> CheckDepositLiabilitiesNonNegativeAsync(CancellationToken ct);
}
