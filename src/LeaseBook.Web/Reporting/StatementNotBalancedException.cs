namespace LeaseBook.Web.Reporting;

/// <summary>
/// Thrown by <see cref="IStatementDelivery"/> when the <see cref="StatementView"/>'s fiduciary
/// tie-out is not balanced (<c>Fiduciary.Balanced == false</c>). Spec §4.1 — "non-zero variance
/// blocks issuance". Mapped to HTTP 409 at the delivery endpoint.
/// <para>
/// No delivery record is written and no artifact is stored when this is thrown — the caller must
/// resolve the variance before re-attempting delivery.
/// </para>
/// </summary>
public sealed class StatementNotBalancedException(Guid ownerId, int year, int month, decimal variance)
    : Exception(
        $"Statement for owner {ownerId} ({year}-{month:D2}) has a non-zero tie-out variance of " +
        $"{variance:0.00}; delivery is blocked until the variance is resolved.")
{
    public Guid OwnerId { get; } = ownerId;
    public int Year { get; } = year;
    public int Month { get; } = month;
    public decimal Variance { get; } = variance;
}
