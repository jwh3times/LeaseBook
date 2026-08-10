using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>Vacuity controls for the two modules every source- and IL-backed guard now shares.</summary>
public sealed class ArchitectureTestInfrastructureTests
{
    [Fact]
    public void Compiled_code_reads_string_literals_from_async_state_machines()
    {
        var expected = string.Concat("architecture", "-il", "-needle");

        CompiledCode.In(typeof(CompiledCodeFixture))
            .ShouldContain(reference =>
                reference.Kind == CompiledReferenceKind.String && reference.Target == expected);
    }

    [Fact]
    public void Compiled_code_reads_called_members()
    {
        CompiledCode.In(typeof(CompiledCodeFixture))
            .ShouldContain(reference =>
                reference.Kind == CompiledReferenceKind.Method &&
                reference.Target.Contains("System.GC::KeepAlive", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("LeaseBook.Modules.Capabilities.Contracts.CapabilitySet")]
    [InlineData("Entitlement")]
    [InlineData("SELECT * FROM feature_flags")]
    [InlineData("System.Boolean FeatureGate::IsEnabled(System.String)")]
    public void Capability_guard_recognizes_every_forbidden_reference_shape(string target)
    {
        var reference = new CompiledReference(
            "Fixture",
            "Fixture::Method()",
            CompiledReferenceKind.String,
            target);

        CapabilityCodeGuard.FindMentions([reference]).ShouldContain(reference);
    }

    [Fact]
    public void Repository_source_finds_the_checkout_and_ignores_whole_line_comments_only()
    {
        RepositorySource.TryLocate().ShouldNotBeNull();

        new RepositoryLine("sample.cs", 1, "  // app.platform").Code.ShouldBe("");
        new RepositoryLine("sample.cs", 2, "Use(\"https://example.test\"); Set(\"app.platform\");").Code
            .ShouldContain("app.platform", Case.Sensitive);
    }

    private sealed class CompiledCodeFixture
    {
        public static async Task<string> AsyncLiteral()
        {
            await Task.Yield();
            return "architecture-il-needle";
        }

        public static void CallMember() => GC.KeepAlive(null);
    }
}
