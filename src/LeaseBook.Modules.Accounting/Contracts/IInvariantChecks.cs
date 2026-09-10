namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// One detected breach of a correctness invariant (§C.7). <see cref="Invariant"/> is the id (e.g.
/// <c>I1</c>); <see cref="Detail"/> is a human-readable description used in the sweep report.
/// </summary>
public sealed record InvariantViolation(string Invariant, string Detail);

/// <summary>
/// The always-true correctness invariants over an org's live journal (§C.7), run by the
/// <c>check-invariants</c> CLI sweep and the test harness. Each returns a list of violations (empty =
/// clean), never just a bool, so a failure names what broke. Basis convergence and void-nets-to-zero
/// are relational/conditional and proven in the test harness, not swept here.
/// <para>
/// <b>Two invariants are numbered "I5".</b> The swept one below is migration-clearing residual; the
/// harness's <c>..._I5</c> is basis convergence. They are different assertions that share a label, so
/// an operator paging on a swept "I5" must not be sent to the basis-convergence runbook entry.
/// Tracked separately — do not resolve it by renumbering here, since the id is what alerting keys on.
/// </para>
/// </summary>
public interface IInvariantChecks
{
    /// <summary>I1: every entry balances per basis. I2: the trust equation holds per trust bank.
    /// I3: no pm_income line carries an owner. I4: every held deposit/prepayment is ≥ 0.
    /// I5 (swept): migration_clearing nets to $0 per basis.
    /// I7: held security deposit is ≥ 0 per (tenant, owner) bucket — dimension symmetry between a
    /// collection and its disposition.
    /// I8: every event_type posting an owner-attributed owner_equity line has a statement section.</summary>
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
