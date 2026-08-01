using Hangfire;
using LeaseBook.Web.Observability;

namespace LeaseBook.Web.Jobs;

/// <summary>
/// The nightly trust-invariant sweep (WP-11) — ADR-001's first scheduler integration. Thin by
/// design: it owns the schedule and the job-level failure contract, while
/// <see cref="ISweepRunner"/> owns what is actually checked.
/// <para>
/// <see cref="AutomaticRetryAttribute"/> is set to zero attempts deliberately. A violation is a data
/// condition, not a transient fault — re-running the same query minutes later cannot clear it and
/// would only multiply the alert. The next night's run is the retry.
/// </para>
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class InvariantSweepJob(
    ISweepRunner runner,
    ILogger<InvariantSweepJob> logger)
{
    /// <summary>Stable recurring-job id — changing it orphans the existing schedule row.</summary>
    public const string JobId = "invariant-sweep";

    /// <summary>07:00 UTC daily: after the nightly window, before a US business day starts.</summary>
    public const string CronUtc = "0 7 * * *";

    public async Task RunAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(orgIds: null, ct);

        if (result.IsClean)
        {
            logger.LogInformation(
                LogEvents.InvariantSweepCompleted,
                "Nightly invariant sweep clean across {OrgCount} org(s).",
                result.OrgsChecked);
            return;
        }

        // Each violation has already been logged individually under LogEvents.InvariantViolation by
        // the runner — that is the alert signal. Throwing here additionally records the run as Failed
        // in Hangfire storage, so an operator reading the job tables (there is no dashboard in
        // Phase 1) sees it without needing the log pipeline to be healthy.
        throw new InvalidOperationException(
            $"Nightly invariant sweep found {result.Violations.Count} violation(s) across " +
            $"{result.OrgsChecked} org(s). See the {nameof(LogEvents.InvariantViolation)} log " +
            "entries for the affected orgs and invariants.");
    }
}
