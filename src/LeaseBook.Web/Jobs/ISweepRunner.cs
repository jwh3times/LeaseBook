using LeaseBook.Modules.Accounting.Contracts;

namespace LeaseBook.Web.Jobs;

/// <summary>
/// One <see cref="InvariantViolation"/> plus the org it was found in — the sweep's unit of report.
/// The CLI verb renders these to the console; the nightly job logs each one under
/// <see cref="Observability.LogEvents.InvariantViolation"/>, which is what Track B alerts on.
/// </summary>
public sealed record SweepViolation(Guid OrgId, string Invariant, string Detail);

/// <summary>Outcome of one sweep across one or more orgs.</summary>
public sealed record SweepResult(int OrgsChecked, IReadOnlyList<SweepViolation> Violations)
{
    public bool IsClean => Violations.Count == 0;
}

/// <summary>
/// The single body of the trust-invariant sweep (§C.7), shared by the <c>check-invariants</c> CLI
/// verb and the nightly Hangfire job (WP-11 / ADR-001) so the two can never drift into checking
/// different things. Everything scheduler-specific — arg parsing, console output, exit codes, job
/// retry policy — stays with the caller.
/// </summary>
public interface ISweepRunner
{
    /// <param name="orgIds">
    /// The orgs to check, or <c>null</c> for every org — the nightly job's mode. Each org runs in its
    /// own <see cref="SharedKernel.Tenancy.OrgScopedExecutor"/> transaction, which throws rather than
    /// letting a missing organization context turn RLS into a silently empty read.
    /// </param>
    Task<SweepResult> RunAsync(IReadOnlyList<Guid>? orgIds = null, CancellationToken ct = default);
}
