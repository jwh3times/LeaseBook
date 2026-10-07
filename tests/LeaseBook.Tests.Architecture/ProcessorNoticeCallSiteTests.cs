using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// A <c>ProcessorNotice</c> means "this callback body proved authentic", and the payment callback
/// routes read whatever one carries without checking again. The type cannot say so by itself: its
/// constructor has to be public, because processor adapters live in the host and the type in the
/// Payments module. So the rule is a placement rule, read from IL: only a method named
/// <c>Authenticate</c> may create one. A caller that built its own would put an unverified body on
/// the anonymous callback path with nothing to stop it.
/// </summary>
public sealed class ProcessorNoticeCallSiteTests
{
    private const string Constructor =
        "System.Void LeaseBook.Modules.Payments.Processing.ProcessorNotice::.ctor(System.Byte[])";

    [Fact]
    public void Only_an_authenticate_step_creates_a_notice()
    {
        var callers = CompiledCode.In(ArchitectureAssemblies.Application)
            .Where(reference =>
                reference.Kind == CompiledReferenceKind.Method &&
                string.Equals(reference.Target, Constructor, StringComparison.Ordinal))
            .ToArray();

        // Vacuity: a renamed type or a changed constructor would leave nothing to match, and the
        // offender check below would pass while guarding nothing.
        callers.ShouldNotBeEmpty($"nothing calls {Constructor}; update this guard to the new shape");

        callers
            .Where(reference => !reference.CallerMethod.Contains("::Authenticate(", StringComparison.Ordinal))
            .Select(reference => reference.ToString())
            .Distinct(StringComparer.Ordinal)
            .ShouldBeEmpty("a payment notice may only come from a processor adapter's Authenticate step");
    }
}
