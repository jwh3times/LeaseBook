using System.Diagnostics;

namespace LeaseBook.SharedKernel.Observability;

/// <summary>
/// The single <see cref="ActivitySource"/> the application emits spans from. The host registers
/// this name with OpenTelemetry (<c>AddSource(LeaseBookTelemetry.SourceName)</c>); without an
/// exporter configured the spans are simply not collected, so this is a no-op locally. Later
/// milestones hang custom money-path events off this same source.
/// </summary>
public static class LeaseBookTelemetry
{
    public const string SourceName = "LeaseBook";

    public static readonly ActivitySource Source = new(SourceName);
}
