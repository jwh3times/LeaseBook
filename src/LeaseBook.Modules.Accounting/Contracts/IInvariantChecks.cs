namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// One detected breach of a correctness invariant (§C.7). <see cref="Invariant"/> is the id (e.g.
/// <c>I1</c>); <see cref="Detail"/> is a human-readable description used in the sweep report.
/// </summary>
public sealed record InvariantViolation(string Invariant, string Detail);

/// <summary>
/// The always-true correctness invariants over an org's live journal (§C.7), run by the
/// <c>check-invariants</c> CLI sweep and the test harness. Each returns a list of violations (empty =
/// clean), never just a bool, so a failure names what broke. I5 (basis convergence) and I6 (a void
/// and its reversal net to zero) are relational/conditional and proven in the test harness, not
/// swept here — an id names one assertion whether it is swept or harness-proven, which is why the
/// migration-clearing check is I9 rather than reusing I5. <c>InvariantIdCollisionTests</c> fails the
/// build if a swept id and a harness id ever name different things again.
/// </summary>
public interface IInvariantChecks
{
    /// <summary>I1: every entry balances per basis. I2: the trust equation holds per trust bank.
    /// I3: no pm_income line carries an owner. I4: every held deposit/prepayment is ≥ 0.
    /// I7: held security deposit is ≥ 0 per (tenant, owner) bucket — dimension symmetry between a
    /// collection and its disposition.
    /// I8: every event_type posting an owner-attributed owner_equity line has a statement section.
    /// I9: migration_clearing nets to $0 per basis.</summary>
    Task<IReadOnlyList<InvariantViolation>> CheckCoreAsync(CancellationToken ct);

    Task<IReadOnlyList<InvariantViolation>> CheckEntriesBalanceAsync(CancellationToken ct);

    Task<IReadOnlyList<InvariantViolation>> CheckTrustEquationAsync(CancellationToken ct);

    Task<IReadOnlyList<InvariantViolation>> CheckPmIncomeIsolationAsync(CancellationToken ct);

    Task<IReadOnlyList<InvariantViolation>> CheckDepositLiabilitiesNonNegativeAsync(CancellationToken ct);

    Task<IReadOnlyList<InvariantViolation>> CheckDepositAttributionSymmetricAsync(CancellationToken ct);

    /// <summary>
    /// I8: reachability, not arithmetic. An owner-equity event type absent from
    /// <c>StatementSectionMap</c> makes every statement covering it throw
    /// <c>UncategorizedEventException</c> rather than silently dropping the line, so the money is
    /// never wrong — the statement simply cannot be produced. Swept because the statement engine's
    /// own tie-out variance cannot be: it is structurally zero for every possible data state, and is
    /// asserted against source edits in the property suite instead.
    /// </summary>
    Task<IReadOnlyList<InvariantViolation>> CheckStatementSectionCoverageAsync(CancellationToken ct);
}
