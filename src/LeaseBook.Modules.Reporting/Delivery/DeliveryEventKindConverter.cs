using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// Explicit <see cref="DeliveryEventKind"/> &lt;-&gt; snake_case-text converter (mirrors
/// <c>AccountingEnumConverters</c> / <c>DirectoryEnumConverters</c> pattern). Strings are a
/// storage contract — spelled out by hand so a rename does not silently break existing rows.
/// </summary>
public sealed class DeliveryEventKindConverter()
    : ValueConverter<DeliveryEventKind, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(DeliveryEventKind value) => value switch
    {
        DeliveryEventKind.Queued => "queued",
        DeliveryEventKind.Accepted => "accepted",
        DeliveryEventKind.Delivered => "delivered",
        DeliveryEventKind.Bounced => "bounced",
        DeliveryEventKind.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown delivery event kind."),
    };

    public static DeliveryEventKind FromDb(string value) => value switch
    {
        "queued" => DeliveryEventKind.Queued,
        "accepted" => DeliveryEventKind.Accepted,
        "delivered" => DeliveryEventKind.Delivered,
        "bounced" => DeliveryEventKind.Bounced,
        "failed" => DeliveryEventKind.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown delivery event kind text."),
    };

    /// <summary>The CHECK-constraint set, single-quoted for inline SQL.</summary>
    public static readonly string[] DbValues =
        ["queued", "accepted", "delivered", "bounced", "failed"];
}
