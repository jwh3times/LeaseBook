using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Development/test implementation of <see cref="IStatementDelivery"/>. Renders the PDF via
/// <see cref="StatementPdf"/>, stores the bytes via <see cref="IArtifactStore"/>, and writes the
/// append-only artifact / attempt / event rows (ADR-040). No provider is contacted, so the history it
/// produces on its own stops at <see cref="DeliveryEventKind.Queued"/>; everything after that arrives
/// through <see cref="RecordEventAsync"/>.
/// <para>
/// <b>Tie-out gate (spec §4.1):</b> <see cref="DeliverAsync"/> throws
/// <see cref="StatementNotBalancedException"/> before any side effect when <c>view.Fiduciary.Balanced</c>
/// is false. The <see cref="AppDbContext"/> SaveChanges interceptor auto-audits the row inserts on the
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
    public async Task<DeliveryAttemptResult> DeliverAsync(
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
        var artifactId = UuidV7.NewId();
        var artifactKey = $"{artifactId:N}.pdf";

        // 3. Store the immutable bytes. Put before the DB write so a crash between the two leaves an
        //    orphaned artifact (recoverable) rather than an artifact row with no bytes behind it
        //    (a harder inconsistency). The reverse order would be worse.
        await artifactStore.PutAsync(pdfBytes, artifactKey, ct);

        // 4. Write the artifact, the first attempt, and that attempt's Queued event as one unit. The
        //    AppDbContext SaveChanges interceptor auto-audits each insert, satisfying the DoD audit
        //    requirement for money-touching paths.
        var artifact = new StatementArtifact
        {
            Id = artifactId,
            OwnerId = view.OwnerId,
            PeriodYear = view.Year,
            PeriodMonth = view.Month,
            Basis = view.Basis,
            ArtifactKey = artifactKey,
        };
        db.Set<StatementArtifact>().Add(artifact);

        var attempt = NewAttempt(artifactId, toEmail);
        await db.SaveChangesAsync(ct);

        // Do NOT log the recipient email: it is PII (CWE-359) and caller-supplied (CWE-117 log
        // forging). The delivery is fully identifiable by ids/owner/period/artifact; the recipient
        // lives only in the access-controlled attempt row (statement_delivery_attempts.to_email).
        logger.LogInformation(
            "Statement issued and queued: artifact={ArtifactId} attempt={AttemptId} owner={OwnerId} " +
            "period={Year}-{Month:D2} key={ArtifactKey}",
            artifactId, attempt.Id, view.OwnerId, view.Year, view.Month, artifactKey);

        return new DeliveryAttemptResult(artifactId, attempt.Id, DeliveryEventKind.Queued);
    }

    public async Task<DeliveryAttemptResult> RetryAsync(
        Guid artifactId, string toEmail, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(toEmail);

        // The org filter is what scopes this read; RLS is the boundary behind it. A wrong-org id
        // therefore reads as "not found" rather than leaking that the artifact exists elsewhere.
        var artifact = await db.Set<StatementArtifact>()
            .SingleOrDefaultAsync(a => a.Id == artifactId, ct)
            ?? throw new InvalidOperationException(
                $"No statement artifact {artifactId} in this organization; cannot retry its delivery.");

        // Deliberately no re-render and no tie-out gate: this artifact was issued through the gate and
        // its bytes are immutable, so the retry sends the identical document.
        var attempt = NewAttempt(artifact.Id, toEmail);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Statement delivery retried: artifact={ArtifactId} attempt={AttemptId} owner={OwnerId} " +
            "period={Year}-{Month:D2}",
            artifact.Id, attempt.Id, artifact.OwnerId, artifact.PeriodYear, artifact.PeriodMonth);

        return new DeliveryAttemptResult(artifact.Id, attempt.Id, DeliveryEventKind.Queued);
    }

    public async Task<DeliveryAttemptResult> RecordEventAsync(
        Guid attemptId,
        DeliveryEventKind kind,
        DateTime occurredAt,
        string? providerMessageId,
        string? detail,
        CancellationToken ct)
    {
        if (kind == DeliveryEventKind.Queued)
        {
            throw new InvalidOperationException(
                "An attempt is queued exactly once, when it is opened. Record a retry as a new " +
                "attempt via RetryAsync rather than a second Queued event.");
        }

        var attempt = await db.Set<StatementDeliveryAttempt>()
            .Include(a => a.Events)
            .SingleOrDefaultAsync(a => a.Id == attemptId, ct)
            ?? throw new InvalidOperationException(
                $"No statement delivery attempt {attemptId} in this organization.");

        // Append at the next position. The unique index on (org_id, attempt_id, sequence) is what
        // makes this safe: a concurrent recorder computing the same next position loses on the index
        // instead of quietly producing two "latest" events.
        var next = attempt.Events.Count == 0 ? 1 : attempt.Events.Max(e => e.Sequence) + 1;

        db.Set<StatementDeliveryEvent>().Add(new StatementDeliveryEvent
        {
            Id = UuidV7.NewId(),
            AttemptId = attempt.Id,
            Sequence = next,
            Kind = kind,
            OccurredAt = occurredAt,
            ProviderMessageId = providerMessageId,
            Detail = detail,
        });

        await db.SaveChangesAsync(ct);

        // Neither the recipient address nor `detail` is logged: detail can echo a provider bounce
        // message that quotes the address back (CWE-359).
        logger.LogInformation(
            "Statement delivery event recorded: attempt={AttemptId} kind={Kind} sequence={Sequence}",
            attempt.Id, DeliveryEventKindConverter.ToDb(kind), next);

        return new DeliveryAttemptResult(attempt.ArtifactId, attempt.Id, kind);
    }

    /// <summary>
    /// Opens an attempt against <paramref name="artifactId"/> together with its <c>Queued</c> event,
    /// tracked but not yet saved. The two are created in one place so no code path can produce an
    /// attempt with an empty history — <see cref="DeliveryStatus"/> treats that as a loading bug.
    /// </summary>
    private StatementDeliveryAttempt NewAttempt(Guid artifactId, string toEmail)
    {
        var attempt = new StatementDeliveryAttempt
        {
            Id = UuidV7.NewId(),
            ArtifactId = artifactId,
            ToEmail = toEmail,
        };
        db.Set<StatementDeliveryAttempt>().Add(attempt);

        db.Set<StatementDeliveryEvent>().Add(new StatementDeliveryEvent
        {
            Id = UuidV7.NewId(),
            AttemptId = attempt.Id,
            Sequence = 1,
            Kind = DeliveryEventKind.Queued,
            OccurredAt = DateTime.UtcNow,
        });

        return attempt;
    }
}
