using LeaseBook.Modules.Reporting.Delivery;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Delivers a rendered owner statement as an immutable PDF artifact and records the delivery
/// attempt. Lives in the host because it depends on <see cref="StatementView"/> (host-owned,
/// ADR-016) and <see cref="StatementPdf"/> (also host-owned).
/// </summary>
public interface IStatementDelivery
{
    /// <summary>
    /// Delivers the statement for <paramref name="view"/> to <paramref name="toEmail"/>.
    /// <list type="bullet">
    /// <item>
    /// <b>Tie-out gate (spec §4.1):</b> if <c>view.Fiduciary.Balanced</c> is false, throws
    /// <see cref="StatementNotBalancedException"/> before rendering or recording anything.
    /// </item>
    /// <item>
    /// On a balanced view: renders the PDF, stores the immutable artifact via
    /// <see cref="IArtifactStore"/>, and writes a <see cref="StatementDeliveryRecord"/> with
    /// state <see cref="DeliveryState.Queued"/>.
    /// </item>
    /// <item>
    /// The live ACS email send and the Queued → Sent/Failed state transition are deferred to M8.
    /// </item>
    /// </list>
    /// </summary>
    /// <exception cref="StatementNotBalancedException">
    /// Thrown when the view's tie-out variance is non-zero. No side effects occur.
    /// </exception>
    Task<DeliveryResult> DeliverAsync(StatementView view, string toEmail, CancellationToken ct);
}

/// <summary>
/// The result of a successful <see cref="IStatementDelivery.DeliverAsync"/> call. State is always
/// <see cref="DeliveryState.Queued"/> until the M8 ACS seam transitions it.
/// </summary>
public sealed record DeliveryResult(Guid Id, DeliveryState State);
