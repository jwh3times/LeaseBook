using System.Diagnostics;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace LeaseBook.Tests.Integration.Observability;

public sealed class ProblemResultsTests
{
    [Theory]
    [InlineData("not_tied")]
    [InlineData("statement_not_balanced")]
    [InlineData("internal_error")]
    public void Problem_emits_code_and_correlation_id_as_extensions(string code)
    {
        // Guard for the 2026-07-19 defect: several emitters set `title` but no `code` extension,
        // so every frontend mapper (which reads body.code) fell through to the raw message.
        var httpContext = new DefaultHttpContext();

        var problem = ProblemResults
            .Problem(httpContext, code, "detail text", StatusCodes.Status409Conflict)
            .ShouldBeOfType<ProblemHttpResult>()
            .ProblemDetails;

        problem.Extensions.ShouldContainKey("code");
        problem.Extensions["code"].ShouldBe(code);
        problem.Extensions.ShouldContainKey("correlationId");
        problem.Title.ShouldBe(code);
    }

    [Fact]
    public void Caller_supplied_extensions_survive_alongside_the_stamped_ones()
    {
        // The P54 contract: LedgerComposer.tsx:87-91 reads existingEntryId to treat a
        // double-submit as success. Stamping code/correlationId must not drop it.
        var existingEntryId = Guid.NewGuid();
        var httpContext = new DefaultHttpContext();

        var problem = ProblemResults.Problem(
                httpContext, "duplicate_source_ref", "This has already been posted.",
                StatusCodes.Status409Conflict,
                new Dictionary<string, object?> { ["existingEntryId"] = existingEntryId })
            .ShouldBeOfType<ProblemHttpResult>()
            .ProblemDetails;

        problem.Extensions["existingEntryId"].ShouldBe(existingEntryId);
        problem.Extensions.ShouldContainKey("code");
        problem.Extensions.ShouldContainKey("correlationId");
    }

    [Fact]
    public void Correlation_id_is_the_ambient_trace_id()
    {
        // The whole point of using the trace id: what the operator quotes IS App Insights'
        // operation_Id. An end-to-end equality test is impossible (the test client cannot see the
        // server's Activity), so this unit seam is the equality test; integration asserts presence.
        using var activity = new Activity("test").Start();

        ProblemResults.CorrelationId(new DefaultHttpContext())
            .ShouldBe(activity.TraceId.ToString());
    }

    [Fact]
    public void Correlation_id_falls_back_to_the_trace_identifier_without_an_activity()
    {
        Activity.Current = null;
        var httpContext = new DefaultHttpContext { TraceIdentifier = "req-123" };

        ProblemResults.CorrelationId(httpContext).ShouldBe("req-123");
    }
}
