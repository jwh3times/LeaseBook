using System.Text.Json;
using LeaseBook.Modules.Operations.Runs;
using Shouldly;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// The shape of <c>bulk_runs.summary_json</c> (ADR-028 / Task 11). Two things are pinned here that a
/// compiler cannot pin on its own.
/// <para>
/// <b>The wire names.</b> <see cref="RunSummary"/> replaced an anonymous object whose members were
/// already camelCase, and every summary row already committed uses those names. Serializing a record
/// with default options emits PascalCase, which would fork the shape silently: old rows would stop
/// being readable by the cross-run guard and the SPA's run history would render empty fields.
/// </para>
/// <para>
/// <b>Writer and reader agree.</b> The guard looks the money-path property up BY STRING on rows
/// written by other processes and other deployments. A rename that moved only one side would turn
/// every comparison into a skip — the guard would go quiet rather than fail, which is the worst
/// failure shape available to it.
/// </para>
/// </summary>
public sealed class RunSummaryTests
{
    private static readonly RunSummary Summary = new(
        Posted: 3,
        Skipped: 1,
        Excluded: 0,
        Total: 4200.50m,
        Capabilities: "v1.token",
        CapabilitiesEnabled: ["alpha"],
        CapabilitiesMoneyPath: ["beta=off"],
        CapabilityChangeAcknowledged: false,
        CapabilityChangeFrom: null);

    [Fact]
    public void The_serialized_property_names_are_the_camel_case_wire_names()
    {
        using var parsed = JsonDocument.Parse(Summary.ToJson());

        parsed.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(
            [
                "posted", "skipped", "excluded", "total", "capabilities", "capabilitiesEnabled",
                "capabilitiesMoneyPath", "capabilityChangeAcknowledged", "capabilityChangeFrom",
            ],
            ignoreOrder: true);
    }

    /// <summary>
    /// The constant the reader looks up must be the name the writer emits. Asserted against the
    /// serialized document rather than against a second literal, so the naming policy is what is
    /// being checked and not one string compared with its own copy.
    /// </summary>
    [Fact]
    public void The_money_path_property_constant_is_the_name_actually_written()
    {
        using var parsed = JsonDocument.Parse(Summary.ToJson());

        parsed.RootElement.TryGetProperty(RunSummary.MoneyPathProperty, out _).ShouldBeTrue();
    }

    /// <summary>
    /// A run that overrode nothing still records the fact. Null is the recorded answer "nothing was
    /// overridden", not an absent field — and the field is present so a reader never has to guess
    /// which of the two it is looking at.
    /// </summary>
    [Fact]
    public void An_unused_override_is_written_as_an_explicit_null()
    {
        using var parsed = JsonDocument.Parse(Summary.ToJson());

        parsed.RootElement.GetProperty("capabilityChangeAcknowledged").GetBoolean().ShouldBeFalse();
        parsed.RootElement.GetProperty("capabilityChangeFrom").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void The_money_path_state_round_trips_through_the_reader()
    {
        RunSummary.ReadMoneyPathState(Summary.ToJson()).ShouldBe(["beta=off"]);
    }

    /// <summary>
    /// The pre-field case the guard skips. Null means "cannot be compared", and the reader must never
    /// invent an empty state for it — an empty state is a real, different answer that a registry with
    /// no money-path capabilities would legitimately record.
    /// </summary>
    [Fact]
    public void A_summary_without_the_property_reads_as_null_not_as_empty()
    {
        RunSummary.ReadMoneyPathState("""{"posted":1,"skipped":0,"excluded":0,"total":0}""")
            .ShouldBeNull();
    }

    /// <summary>
    /// And an EMPTY recorded array is not null: a deployment whose registry has no money-path
    /// capability records exactly this, and it must compare equal to another such run rather than
    /// being skipped as unreadable.
    /// </summary>
    [Fact]
    public void An_empty_recorded_state_reads_as_empty_not_as_null()
    {
        RunSummary.ReadMoneyPathState("""{"capabilitiesMoneyPath":[]}""").ShouldBe([]);
    }
}
