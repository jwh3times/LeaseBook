using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <summary>
    /// ADR-039: actor attribution becomes durable. <c>journal_entries</c> and <c>audit_events</c>
    /// gain <c>actor_kind</c> + <c>actor_process</c>, so a historical row can say WHICH system
    /// process wrote it instead of leaving a null that is indistinguishable from a missing actor.
    /// <para>
    /// <b>Deliberately no backfill.</b> Existing rows keep a null <c>actor_kind</c>, and the check
    /// constraints admit that arm explicitly. There is no honest value to write: for a row already
    /// in the table, the process that wrote it is exactly the fact that was never recorded, and
    /// inventing one would make the audit trail confidently wrong rather than visibly incomplete.
    /// The columns are therefore nullable rather than NOT NULL — the invariant is enforced at the
    /// write path (AppDbContext and PostingService both refuse an absent actor), and the constraint
    /// enforces the pairing for every row that has a kind at all.
    /// </para>
    /// <para>
    /// A consequence worth stating: no DML here, so nothing to bracket with NO FORCE ROW LEVEL
    /// SECURITY. A backfill would have needed it (see the v0.7.0 defect and MigrationBackfillRlsTests).
    /// </para>
    /// </summary>
    public partial class M8_PersistActorAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor_kind",
                table: "journal_entries",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor_process",
                table: "journal_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor_kind",
                table: "audit_events",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor_process",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_journal_entries_actor_attribution",
                table: "journal_entries",
                sql: "actor_kind IS NULL\nOR (actor_kind = 'user' AND created_by IS NOT NULL AND actor_process IS NULL)\nOR (actor_kind = 'system' AND created_by IS NULL AND actor_process IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_actor_attribution",
                table: "audit_events",
                sql: "actor_kind IS NULL\nOR (actor_kind = 'user' AND actor_user_id IS NOT NULL AND actor_process IS NULL)\nOR (actor_kind = 'system' AND actor_user_id IS NULL AND actor_process IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_journal_entries_actor_attribution",
                table: "journal_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_actor_attribution",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "actor_kind",
                table: "journal_entries");

            migrationBuilder.DropColumn(
                name: "actor_process",
                table: "journal_entries");

            migrationBuilder.DropColumn(
                name: "actor_kind",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "actor_process",
                table: "audit_events");
        }
    }
}
