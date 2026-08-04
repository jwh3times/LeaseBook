using System.Text.Json;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// The shape of <c>bulk_runs.summary_json</c>. A NAMED record rather than the anonymous object this
/// started as, because one of its fields is load-bearing for a guard that reads it back.
/// <para>
/// <b>Why the shape has to be compiler-enforced.</b> <see cref="RunEngine"/>'s cross-run guard skips
/// a prior run whose summary carries no <see cref="CapabilitiesMoneyPath"/>, on the reasoning that a
/// field-less run must predate the field. That reasoning holds only while every committed
/// <c>BulkRun</c> writes it: the guard reads the MOST RECENT prior run, so if some other writer ever
/// produced a field-less header, a field-less run could be the most recent rather than necessarily
/// the oldest, and the skip would reopen permanently rather than aging out. An anonymous object made
/// that a convention nobody could see from the other writer's side. A record with no optional
/// members makes omitting the field a compile error.
/// </para>
/// <para>
/// <b>Every member is required on purpose</b>, including <see cref="CapabilityChangeFrom"/>, which is
/// null on the ordinary path. Null is the recorded answer "nothing was overridden", not an absent
/// field, and a default would let a future call site skip stating it.
/// </para>
/// </summary>
/// <param name="Capabilities">
/// The opaque version token of the set the run resolved. Recorded for the audit trail; the cross-run
/// guard deliberately does NOT compare it — see <c>RunCapabilities.MoneyPathNames</c>.
/// </param>
/// <param name="CapabilitiesEnabled">The readable half: every capability that resolved "on".</param>
/// <param name="CapabilitiesMoneyPath">
/// The money-path state this run posted under, as ordered <c>name=on</c> / <c>name=off</c> entries.
/// What the NEXT run for this period compares itself against.
/// </param>
/// <param name="CapabilityChangeAcknowledged">
/// True when this run proceeded past a differing prior state on an explicit acknowledgement. Always
/// written, so "the override was not used" is a recorded fact rather than the absence of one.
/// </param>
/// <param name="CapabilityChangeFrom">
/// The prior state that was overridden, or null when nothing was. Lets an auditor read both halves of
/// a period computed two ways off the run rows, without replaying run history.
/// </param>
internal sealed record RunSummary(
    int Posted,
    int Skipped,
    int Excluded,
    decimal Total,
    string Capabilities,
    IReadOnlyList<string> CapabilitiesEnabled,
    IReadOnlyList<string> CapabilitiesMoneyPath,
    bool CapabilityChangeAcknowledged,
    IReadOnlyList<string>? CapabilityChangeFrom)
{
    /// <summary>
    /// The wire name of <see cref="CapabilitiesMoneyPath"/>. Named once and shared by the writer and
    /// the reader: <see cref="RunEngine.ReadPriorMoneyPathStateAsync"/> looks this property up by
    /// string on rows written by other processes and other deployments, so a rename that moved only
    /// one side would silently turn every comparison into a skip.
    /// </summary>
    public const string MoneyPathProperty = "capabilitiesMoneyPath";

    /// <summary>
    /// camelCase, matching the anonymous object this replaced byte-for-byte — the SPA renders
    /// <c>summary_json</c> from run history, and every summary already committed uses these names.
    /// Serializing a record with default options would emit PascalCase and silently fork the shape.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Reads <see cref="MoneyPathProperty"/> back out of a persisted summary, or null when the row
    /// does not carry it — a run committed before the field existed. Null is "cannot be compared",
    /// never "compared as empty": see <see cref="RunEngine.ReadPriorMoneyPathStateAsync"/>.
    /// </summary>
    public static IReadOnlyList<string>? ReadMoneyPathState(string summaryJson)
    {
        using var parsed = JsonDocument.Parse(summaryJson);

        return parsed.RootElement.TryGetProperty(MoneyPathProperty, out var recorded) &&
               recorded.ValueKind == JsonValueKind.Array
            ? recorded.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
            : null;
    }
}
