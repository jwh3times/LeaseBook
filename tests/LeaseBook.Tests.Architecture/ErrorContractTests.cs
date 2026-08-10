using System.Text.RegularExpressions;
using LeaseBook.SharedKernel.Endpoints;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// ADR-025's single-factory rule, enforced: every problem-details response is built by
/// ProblemResults. 28 pre-existing direct call sites are the proof that a doc-comment convention
/// does not hold on its own. Reflection cannot see call sites; the compiled-code module reads those
/// method references from IL without pinning this rule to source formatting or paths.
/// </summary>
public sealed class ErrorContractTests
{
    private static readonly Regex Forbidden = new(
        @"Microsoft\.AspNetCore\.Http\.(?:TypedResults|Results)::(?:Problem|ValidationProblem)\(",
        RegexOptions.Compiled);

    [Fact]
    public void Only_ProblemResults_builds_problem_details_responses()
    {
        var factoryType = typeof(ProblemResults).FullName.ShouldNotBeNull();
        var references = CompiledCode.In(ArchitectureAssemblies.Application);
        references.ShouldContain(
            reference =>
                reference.Kind == CompiledReferenceKind.Method &&
                Forbidden.IsMatch(reference.Target) &&
                reference.CallerType == factoryType,
            "ProblemResults must still contain the sanctioned framework calls; otherwise this guard " +
            "could pass because the IL reader or target pattern stopped seeing its subject");

        var offenders = references
            .Where(reference =>
                reference.Kind == CompiledReferenceKind.Method &&
                Forbidden.IsMatch(reference.Target) &&
                reference.CallerType != factoryType)
            .Select(reference => reference.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "route these through ProblemResults (ADR-025) — direct Problem/ValidationProblem calls " +
            "ship responses without code/correlationId");
    }
}
