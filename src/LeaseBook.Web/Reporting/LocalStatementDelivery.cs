using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Persistence;
using Microsoft.Extensions.Logging;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Development/test implementation of <see cref="IStatementDelivery"/>. Renders the PDF via
/// <see cref="StatementPdf"/>, stores the artifact via <see cref="IArtifactStore"/>, and writes a
/// <see cref="StatementDeliveryRecord"/> with state <see cref="DeliveryState.Queued"/>.
/// <para>
/// <b>Tie-out gate (spec §4.1):</b> throws <see cref="StatementNotBalancedException"/> before any
/// side effect when <c>view.Fiduciary.Balanced</c> is false. The <see cref="AppDbContext"/>
/// SaveChanges interceptor auto-audits the <see cref="StatementDeliveryRecord"/> insert on the
/// balanced path; there is no entity write on the blocked path.
/// </para>
/// <para>
/// Lives in the host because it directly references <see cref="StatementPdf"/> and
/// <see cref="StatementView"/> (both host-owned, ADR-016 / WP-4 precedent).
/// </para>
/// </summary>
public sealed class LocalStatementDelivery(
    IArtifactStore artifactStore,
    AppDbContext db,
    ILogger<LocalStatementDelivery> logger) : IStatementDelivery
{
    public async Task<DeliveryResult> DeliverAsync(
        StatementView view, string toEmail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentException.ThrowIfNullOrEmpty(toEmail);

        // ── Tie-out gate (spec §4.1 — "non-zero variance blocks issuance") ──────
        // A non-zero variance means the accounting engine's trust equation does not balance for this
        // owner/period. Issuing the statement would expose a fiduciarily incorrect figure to the
        // owner. Throw BEFORE rendering or recording anything so no artifact, no DB row.
        if (!view.Fiduciary.Balanced)
        {
            // No entity write occurs on this path, so the SaveChanges interceptor won't auto-audit.
            // Emit a structured log at Warning level as the alert signal (M3/ADR-010 actor
            // attribution is not available without a DB write; a future M8 alert channel can hook
            // onto this log event by EventId). Note: do NOT throw before logging — callers map the
            // exception to HTTP 409 and swallow the details.
            logger.LogWarning(
                "Statement delivery blocked: tie-out variance {Variance:0.00} for owner {OwnerId} " +
                "period {Year}-{Month:D2}. No artifact written, no delivery record created.",
                view.Fiduciary.Variance, view.OwnerId, view.Year, view.Month);

            throw new StatementNotBalancedException(
                view.OwnerId, view.Year, view.Month, view.Fiduciary.Variance);
        }

        // ── Happy path: balanced statement ────────────────────────────────────────

        // 1. Render the PDF (stateless — StatementPdf.Render is thread-safe).
        var pdfBytes = StatementPdf.Render(view);

        // 2. Derive an immutable artifact key: UUID-v7 for collision resistance.
        //    The key is stored on the delivery record for later retrieval.
        var deliveryId = UuidV7.NewId();
        var artifactKey = $"{deliveryId:N}.pdf";

        // 3. Store the immutable artifact. Put before the DB write so a crash between the two
        //    leaves an orphaned artifact (recoverable) rather than a delivery row with no artifact
        //    (a harder inconsistency). The reverse order would be worse.
        await artifactStore.PutAsync(pdfBytes, artifactKey, ct);

        // 4. Write the delivery record. The AppDbContext SaveChanges interceptor auto-audits this
        //    insert (entity type = "statement_deliveries", action = "insert", after = row snapshot).
        //    This satisfies the DoD audit requirement for money-touching paths.
        var record = new StatementDeliveryRecord
        {
            Id = deliveryId,
            OwnerId = view.OwnerId,
            PeriodYear = view.Year,
            PeriodMonth = view.Month,
            ToEmail = toEmail,
            State = DeliveryState.Queued,
            ArtifactKey = artifactKey,
        };

        db.Set<StatementDeliveryRecord>().Add(record);
        await db.SaveChangesAsync(ct);

        // M8: ACS Email send → transition Queued→Sent/Failed
        // When ACS is wired: call the email sender here, await the acceptance response,
        // then update record.State to Sent or Failed and save again. The artifact key is
        // already stored so the M8 worker can retrieve the bytes independently of this request.

        // Do NOT log the recipient email: it is PII (CWE-359) and caller-supplied (CWE-117 log
        // forging). The delivery is fully identifiable by id/owner/period/artifact; the recipient
        // lives only in the access-controlled delivery record (statement_deliveries.to_email).
        logger.LogInformation(
            "Statement delivery queued: id={DeliveryId} owner={OwnerId} period={Year}-{Month:D2} artifact={ArtifactKey}",
            deliveryId, view.OwnerId, view.Year, view.Month, artifactKey);

        return new DeliveryResult(deliveryId, record.State);
    }
}
