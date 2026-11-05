namespace LeaseBook.Tests.Common;

/// <summary>A clock stopped at one instant.</summary>
public sealed class StoppedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
