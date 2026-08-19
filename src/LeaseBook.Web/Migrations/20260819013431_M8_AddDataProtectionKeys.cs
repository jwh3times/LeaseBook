using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LeaseBook.Web.Migrations
{
    /// <summary>
    /// F8 / ADR-041 — the Data Protection keyring's storage. The default filesystem keyring does not
    /// survive a container recreate, and losing it makes every encrypted
    /// <c>asp_net_user_tokens.value</c> row permanently undecryptable, locking every MFA-enrolled
    /// account out.
    /// <para>
    /// Three deliberate departures from the usual table conventions, none of them oversights:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>No <c>org_id</c>, no RLS.</b> The keyring belongs to the deployment, not to an
    /// organization. It is global-class like <c>orgs</c> and <c>feature_flags</c>, and is allowlisted
    /// in <c>SchemaGuardTests</c> accordingly. It holds no organization data.
    /// </item>
    /// <item>
    /// <b>No <c>RevokeAppendOnly</c>.</b> Data Protection revokes a key by rewriting its XML, so the
    /// runtime role keeps its default DML grants. This is operational state, not a fiduciary record.
    /// </item>
    /// <item>
    /// <b>An identity <c>int</c> primary key</b>, against the UUID-v7 convention. The shape is
    /// <c>DataProtectionKey</c>'s, defined by the framework — not ours to choose.
    /// </item>
    /// </list>
    /// </summary>
    public partial class M8_AddDataProtectionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_protection_keys");
        }
    }
}
