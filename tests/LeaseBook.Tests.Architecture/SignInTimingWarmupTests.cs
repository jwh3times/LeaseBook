using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The sign-in timing equalizer's decoy hash is minted at startup on purpose — deferring it would
/// put a one-off PBKDF2 cost on the first sign-in, which is the exact timing anomaly the equalizer
/// exists to remove. That makes the warm-up load-bearing for the Web host and pure waste anywhere
/// else: neither a CLI verb nor the build-time OpenAPI run ever reaches the point of serving a
/// sign-in, so a hash minted there is one nothing can read (#367).
/// <para>
/// So the rule is a placement rule, and it is enforced from IL rather than from source text: the
/// call that regressed here was an ordinary unconditional statement in the composition root, and a
/// path-based scan would have to guess which file the composition root lives in. Reading method
/// references also survives renaming the file, aliasing the type, or wrapping the call in a helper.
/// </para>
/// <para>
/// This guard says <i>where</i> the warm-up is called from. That it is called only in Web mode is a
/// runtime property, pinned by <c>HostProcessLifecycleTests</c> on both arms.
/// </para>
/// </summary>
public sealed class SignInTimingWarmupTests
{
    private const string WarmCall = "System.Void LeaseBook.Web.Auth.PasswordTimingEqualizer::Warm()";

    // The mode-gated Web startup step. Matched on substrings of the caller's full name rather than
    // on an exact match, because the method is async: the IL caller is its compiler-generated state
    // machine, nested inside the lifecycle and named after the method it was generated from.
    //
    // Both halves are required. The method name alone would sanction a PrepareWebHostAsync on any
    // type — including one nothing gates on process mode — and the type name alone would sanction
    // any other member of the lifecycle, several of which run in every mode.
    private const string SanctionedCallerType = "HostProcessLifecycle";

    private const string SanctionedCallerMethod = "PrepareWebHostAsync";

    [Fact]
    public void Only_the_mode_gated_web_startup_step_warms_the_sign_in_timing_equalizer()
    {
        var callers = CompiledCode.In(ArchitectureAssemblies.Application)
            .Where(reference =>
                reference.Kind == CompiledReferenceKind.Method &&
                string.Equals(reference.Target, WarmCall, StringComparison.Ordinal))
            .ToArray();

        // Vacuity: if the warm-up were deleted, or Warm renamed, the offender check below would pass
        // trivially while the property it protects was gone.
        callers.ShouldNotBeEmpty(
            $"nothing calls {WarmCall} — the Web host must still warm the equalizer before it binds, " +
            "or the first sign-in of a process becomes the one request with anomalous timing");

        var offenders = callers
            .Where(reference =>
                !reference.CallerType.Contains(SanctionedCallerType, StringComparison.Ordinal) ||
                !reference.CallerMethod.Contains(SanctionedCallerMethod, StringComparison.Ordinal))
            .Select(reference => reference.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"warm the equalizer only from {SanctionedCallerType}.{SanctionedCallerMethod} (#367) — a call in " +
            "the composition root runs for every CLI verb and the OpenAPI build too, which mint a " +
            "PBKDF2 hash they can never use" +
            (offenders.Length == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, offenders)));
    }
}
