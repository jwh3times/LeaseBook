using System;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <summary>
    /// ADR-040 — replaces the single mutable <c>statement_deliveries</c> row with the three
    /// append-only tables that separate what was issued from what was attempted from what happened:
    /// <c>statement_artifacts</c>, <c>statement_delivery_attempts</c>, <c>statement_delivery_events</c>.
    /// <para>
    /// The old table deliberately kept its <c>UPDATE</c> grant, because a <c>queued → sent | failed</c>
    /// transition needed one. Nothing here does, so all three tables get <c>RevokeAppendOnly</c> —
    /// the append-only claim the old entity documentation made is now enforced by the grants.
    /// </para>
    /// </summary>
    public partial class M8_SplitStatementDeliveryIntoArtifactAttemptEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "statement_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_year = table.Column<int>(type: "integer", nullable: false),
                    period_month = table.Column<int>(type: "integer", nullable: false),
                    basis = table.Column<string>(type: "text", nullable: true),
                    artifact_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_artifacts", x => x.id);
                    table.UniqueConstraint("ak_statement_artifacts_org_id_id", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_statement_artifacts_basis", "basis IS NULL OR basis IN ('cash', 'accrual')");
                });

            migrationBuilder.CreateTable(
                name: "statement_delivery_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_email = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_delivery_attempts", x => x.id);
                    table.UniqueConstraint("ak_statement_delivery_attempts_org_id_id", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "fk_statement_delivery_attempts_statement_artifacts_org_id_arti",
                        columns: x => new { x.org_id, x.artifact_id },
                        principalTable: "statement_artifacts",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "statement_delivery_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    provider_message_id = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_delivery_events", x => x.id);
                    table.CheckConstraint("ck_statement_delivery_events_kind", "kind IN ('queued', 'accepted', 'delivered', 'bounced', 'failed')");
                    table.CheckConstraint("ck_statement_delivery_events_sequence", "sequence >= 1");
                    table.ForeignKey(
                        name: "fk_statement_delivery_events_statement_delivery_attempts_org_i",
                        columns: x => new { x.org_id, x.attempt_id },
                        principalTable: "statement_delivery_attempts",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_statement_artifacts_org_id_owner_id_period_year_period_month",
                table: "statement_artifacts",
                columns: new[] { "org_id", "owner_id", "period_year", "period_month" });

            migrationBuilder.CreateIndex(
                name: "ix_statement_delivery_attempts_org_id_artifact_id_created_at",
                table: "statement_delivery_attempts",
                columns: new[] { "org_id", "artifact_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_statement_delivery_events_org_id_attempt_id_sequence",
                table: "statement_delivery_events",
                columns: new[] { "org_id", "attempt_id", "sequence" },
                unique: true);

            // ── Carry the existing rows forward ───────────────────────────────────────────────
            // Each old row becomes one artifact + one attempt + its event history. FORCE ROW LEVEL
            // SECURITY binds the migrator, and a migration transaction sets no app.org_id, so the
            // read side of every statement below would otherwise match zero rows without raising.
            // The write side needs no lift: RLS is enabled on the three new tables *after* this
            // block, so their WITH CHECK does not exist yet.
            //
            // Derived ids rather than gen_random_uuid(): the event rows have to point at the attempt
            // rows, and md5(source id || role) gives each statement the same answer without a CTE.
            // Basis is left NULL — statement_deliveries never recorded it, and guessing 'cash' would
            // put a fabricated fact on a fiduciary document (same posture as ADR-039's actor rows).
            migrationBuilder.Sql("""
                ALTER TABLE statement_deliveries NO FORCE ROW LEVEL SECURITY;

                INSERT INTO statement_artifacts
                    (id, org_id, owner_id, period_year, period_month, basis, artifact_key, created_at)
                SELECT d.id, d.org_id, d.owner_id, d.period_year, d.period_month,
                       NULL, d.artifact_key, d.created_at
                FROM statement_deliveries d;

                INSERT INTO statement_delivery_attempts
                    (id, org_id, artifact_id, to_email, created_at)
                SELECT md5(d.id::text || ':attempt')::uuid, d.org_id, d.id, d.to_email, d.created_at
                FROM statement_deliveries d;

                INSERT INTO statement_delivery_events
                    (id, org_id, attempt_id, sequence, kind, occurred_at,
                     provider_message_id, detail, created_at)
                SELECT md5(d.id::text || ':event:1')::uuid, d.org_id,
                       md5(d.id::text || ':attempt')::uuid, 1, 'queued', d.created_at,
                       NULL, NULL, d.created_at
                FROM statement_deliveries d;

                INSERT INTO statement_delivery_events
                    (id, org_id, attempt_id, sequence, kind, occurred_at,
                     provider_message_id, detail, created_at)
                SELECT md5(d.id::text || ':event:2')::uuid, d.org_id,
                       md5(d.id::text || ':attempt')::uuid, 2,
                       CASE d.state WHEN 'sent' THEN 'accepted' ELSE d.state END,
                       d.created_at, NULL, 'carried over from statement_deliveries.state',
                       d.created_at
                FROM statement_deliveries d
                WHERE d.state <> 'queued';

                ALTER TABLE statement_deliveries FORCE ROW LEVEL SECURITY;
                """);

            // RLS after the backfill, for the reason above. Append-only for real this time: unlike
            // statement_deliveries, no path here ever rewrites a row.
            migrationBuilder.EnableOrgRls("statement_artifacts");
            migrationBuilder.EnableOrgRls("statement_delivery_attempts");
            migrationBuilder.EnableOrgRls("statement_delivery_events");

            migrationBuilder.RevokeAppendOnly("statement_artifacts");
            migrationBuilder.RevokeAppendOnly("statement_delivery_attempts");
            migrationBuilder.RevokeAppendOnly("statement_delivery_events");

            migrationBuilder.DropTable(
                name: "statement_deliveries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "statement_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_month = table.Column<int>(type: "integer", nullable: false),
                    period_year = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    to_email = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_deliveries", x => x.id);
                    table.CheckConstraint("ck_statement_deliveries_state", "state IN ('queued', 'sent', 'failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_statement_deliveries_org_id_owner_id_period_year_period_mon",
                table: "statement_deliveries",
                columns: new[] { "org_id", "owner_id", "period_year", "period_month" });

            // ── Collapse the history back into one row per attempt ────────────────────────────
            // Lossy by construction, which is the point of the ADR: the old vocabulary has no way to
            // say "the provider accepted it but the recipient bounced it", so accepted and delivered
            // both fold into 'sent' and bounced folds into 'failed'. Retries survive only as extra
            // rows for the same owner and period, which is exactly the ambiguity #185 named.
            //
            // All three read tables are RLS-forced, so each is lifted and restored here. The insert
            // target is not: RLS is enabled on statement_deliveries below, after this block.
            migrationBuilder.Sql("""
                ALTER TABLE statement_artifacts NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE statement_delivery_attempts NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE statement_delivery_events NO FORCE ROW LEVEL SECURITY;

                INSERT INTO statement_deliveries
                    (id, org_id, owner_id, period_year, period_month, to_email, state,
                     artifact_key, created_at)
                SELECT att.id, att.org_id, art.owner_id, art.period_year, art.period_month,
                       att.to_email,
                       CASE latest.kind
                           WHEN 'queued' THEN 'queued'
                           WHEN 'accepted' THEN 'sent'
                           WHEN 'delivered' THEN 'sent'
                           ELSE 'failed'
                       END,
                       art.artifact_key, att.created_at
                FROM statement_delivery_attempts att
                JOIN statement_artifacts art
                  ON art.org_id = att.org_id AND art.id = att.artifact_id
                JOIN LATERAL (
                    SELECT ev.kind
                    FROM statement_delivery_events ev
                    WHERE ev.org_id = att.org_id AND ev.attempt_id = att.id
                    ORDER BY ev.sequence DESC
                    LIMIT 1
                ) latest ON true;

                ALTER TABLE statement_artifacts FORCE ROW LEVEL SECURITY;
                ALTER TABLE statement_delivery_attempts FORCE ROW LEVEL SECURITY;
                ALTER TABLE statement_delivery_events FORCE ROW LEVEL SECURITY;
                """);

            // The pre-ADR-040 posture, restored in full: org RLS, and no RevokeAppendOnly, because
            // the state column this table is coming back with is the thing that got updated.
            migrationBuilder.EnableOrgRls("statement_deliveries");

            migrationBuilder.DropTable(
                name: "statement_delivery_events");

            migrationBuilder.DropTable(
                name: "statement_delivery_attempts");

            migrationBuilder.DropTable(
                name: "statement_artifacts");
        }
    }
}
