using LeaseBook.Modules.Reporting.Delivery;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Issues owner statements as immutable artifacts and records their delivery history (ADR-040).
/// Lives in the host because it depends on <see cref="StatementView"/> (host-owned, ADR-016) and
/// <see cref="StatementPdf"/> (also host-owned).
/// <para>
/// Everything this seam writes is append-only. There is no method that changes a row: a send is a new
/// <see cref="StatementDeliveryAttempt"/>, and what became of it is a new
/// <see cref="StatementDeliveryEvent"/>. The provider integration (Track B) plugs in at
/// <see cref="RecordEventAsync"/> and needs no other write path.
/// </para>
/// </summary>
public interface IStatementDelivery
{
    /// <summary>
    /// Issues the statement for <paramref name="view"/> and makes the first attempt to send it to
    /// <paramref name="toEmail"/>.
    /// <list type="bullet">
    /// <item>
    /// <b>Tie-out gate (spec §4.1):</b> if <c>view.Fiduciary.Balanced</c> is false, throws
    /// <see cref="StatementNotBalancedException"/> before rendering or recording anything.
    /// </item>
    /// <item>
    /// On a balanced view: renders the PDF, stores the immutable artifact via
    /// <see cref="IArtifactStore"/>, records a <see cref="StatementArtifact"/>, opens one
    /// <see cref="StatementDeliveryAttempt"/> against it, and records that attempt's
    /// <see cref="DeliveryEventKind.Queued"/> event.
    /// </item>
    /// <item>
    /// The live provider send, and the <see cref="DeliveryEventKind.Accepted"/> /
    /// <see cref="DeliveryEventKind.Delivered"/> / <see cref="DeliveryEventKind.Bounced"/> /
    /// <see cref="DeliveryEventKind.Failed"/> events that follow it, arrive through
    /// <see cref="RecordEventAsync"/> when Track B wires ACS.
    /// </item>
    /// </list>
    /// </summary>
    /// <exception cref="StatementNotBalancedException">
    /// Thrown when the view's tie-out variance is non-zero. No side effects occur.
    /// </exception>
    Task<DeliveryAttemptResult> DeliverAsync(StatementView view, string toEmail, CancellationToken ct);

    /// <summary>
    /// Opens a <b>new</b> attempt to send an already-issued artifact, optionally to a corrected
    /// address. Records the new attempt's <see cref="DeliveryEventKind.Queued"/> event.
    /// <para>
    /// The artifact is not re-rendered, so a retry cannot deliver different figures than the attempt
    /// it follows — that is the whole reason artifact and attempt are separate rows. It is also why
    /// the tie-out gate does not run again here: the gate governs <i>issuing</i> a statement, and this
    /// artifact was already issued through it.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No artifact with that id is visible in the current organization.
    /// </exception>
    Task<DeliveryAttemptResult> RetryAsync(Guid artifactId, string toEmail, CancellationToken ct);

    /// <summary>
    /// Appends one fact to an attempt's history — what the provider or the recipient's server did.
    /// Returns the attempt's status after the append, which is simply this event's kind.
    /// </summary>
    /// <param name="occurredAt">
    /// When the reporter says it happened. Stored as evidence; it does not order the history.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// No attempt with that id is visible in the current organization, or
    /// <paramref name="kind"/> is <see cref="DeliveryEventKind.Queued"/> — an attempt is queued
    /// exactly once, when it is opened.
    /// </exception>
    Task<DeliveryAttemptResult> RecordEventAsync(
        Guid attemptId,
        DeliveryEventKind kind,
        DateTime occurredAt,
        string? providerMessageId,
        string? detail,
        CancellationToken ct);
}

/// <summary>
/// What a delivery call produced: the artifact that was (or already had been) issued, the attempt
/// that carries this send, and that attempt's current status — the kind of its latest recorded event
/// (ADR-040), never a stored column.
/// <para>
/// <see cref="Status"/> is <see cref="DeliveryEventKind.Queued"/> for a fresh attempt. It is
/// deliberately not named <c>Sent</c> in any state: the provider accepting a message is
/// <see cref="DeliveryEventKind.Accepted"/>, and only <see cref="DeliveryEventKind.Delivered"/> means
/// the recipient's server took it.
/// </para>
/// </summary>
public sealed record DeliveryAttemptResult(Guid ArtifactId, Guid AttemptId, DeliveryEventKind Status);
