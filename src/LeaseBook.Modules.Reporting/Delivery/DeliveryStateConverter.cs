using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// Explicit <see cref="DeliveryState"/> &lt;-&gt; snake_case-text converter (mirrors
/// <c>AccountingEnumConverters</c> / <c>DirectoryEnumConverters</c> pattern). Strings are a
/// storage contract — spelled out by hand so a rename does not silently break existing rows.
/// </summary>
public sealed class DeliveryStateConverter()
    : ValueConverter<DeliveryState, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(DeliveryState value) => value switch
    {
        DeliveryState.Queued => "queued",
        DeliveryState.Sent => "sent",
        DeliveryState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown delivery state."),
    };

    public static DeliveryState FromDb(string value) => value switch
    {
        "queued" => DeliveryState.Queued,
        "sent" => DeliveryState.Sent,
        "failed" => DeliveryState.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown delivery state text."),
    };

    /// <summary>The CHECK-constraint set, single-quoted for inline SQL.</summary>
    public static readonly string[] DbValues = ["queued", "sent", "failed"];
}
