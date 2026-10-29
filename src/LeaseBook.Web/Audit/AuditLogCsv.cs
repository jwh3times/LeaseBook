using System.Globalization;
using System.Text;
using CsvHelper;
using LeaseBook.SharedKernel.Csv;

namespace LeaseBook.Web.Audit;

/// <summary>
/// The audit-review list rendered to CSV (#321) — the same filtered rows the reviewer is reading, in the
/// form they can hand to someone else. Metadata only, exactly like the list: the payload diff stays behind
/// the per-event read, so an export can never carry a snapshot the drawer would have redacted.
/// <para>
/// Every cell routes through <see cref="CsvFormulaGuard"/>, like every other LeaseBook export. An
/// <c>entity_type</c> or an actor display name is text a spreadsheet would otherwise evaluate.
/// </para>
/// </summary>
public static class AuditLogCsv
{
    // CsvHelper 34 rejects records of different lengths and no longer quotes a field for a leading or trailing
    // space. This export mixes title, header and data rows, and keeps the quoting CsvHelper 33 gave it: a
    // field with the separator, a quote or a line break, or one that starts or ends with a space.
    private static readonly CsvWriterOptions Options = new()
    {
        ValidateFieldCount = false,
        ShouldQuote = (field, _, _, _) => field.Length > 0 && (field[0] == ' ' || field[^1] == ' '),
    };

    /// <summary>
    /// The most rows one export writes. An audit trail has no natural size, and a reviewer who asks for
    /// "everything" would otherwise stream the largest table in the database through a browser download.
    /// A truncated export says so in its title row rather than looking complete.
    /// </summary>
    public const int MaxRows = 10_000;

    public static byte[] Write(IReadOnlyList<AuditLogRow> rows, int total)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = CsvWriter.Create(writer, Options, leaveOpen: true))
        {
            csv.WriteField(Title(rows.Count, total));
            csv.EndRecord();

            foreach (var column in Columns)
            {
                csv.WriteField(column);
            }

            csv.EndRecord();

            foreach (var row in rows)
            {
                foreach (var cell in Cells(row))
                {
                    csv.WriteField(CsvFormulaGuard.Neutralize(cell));
                }

                csv.EndRecord();
            }
        }

        return Encoding.UTF8.GetBytes(writer.ToString());
    }

    private static readonly string[] Columns =
        ["Occurred (UTC)", "Entity type", "Entity id", "Action", "Actor", "Actor email"];

    private static string Title(int written, int total) =>
        written < total
            ? $"Audit log — {written} of {total} events (truncated at the {MaxRows}-row export limit; narrow the filters to export the rest)"
            : $"Audit log — {written} events";

    private static IEnumerable<string> Cells(AuditLogRow row) =>
    [
        row.OccurredAt.ToString("u", CultureInfo.InvariantCulture),
        row.EntityType,
        row.EntityId.ToString(),
        row.Action,
        row.ActorName,
        row.ActorEmail ?? "",
    ];
}
