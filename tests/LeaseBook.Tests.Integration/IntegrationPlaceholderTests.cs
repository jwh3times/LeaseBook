using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Placeholder. WP-04 replaces this with the Testcontainers Postgres fixture and the round-trip
/// test; WP-05 adds the tenant-isolation pack and schema guard; WP-06 adds the auth tests.
/// </summary>
public sealed class IntegrationPlaceholderTests
{
    [Fact]
    public void Host_assembly_is_referenceable()
    {
        typeof(Program).Assembly.GetName().Name.ShouldBe("LeaseBook.Web");
    }
}
