using LeaseBook.Web.Endpoints;
using LeaseBook.Web.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace LeaseBook.Tests.Integration.Observability;

public sealed class UnhandledExceptionHandlerTests
{
    [Fact]
    public async Task Unhandled_exception_returns_a_safe_generic_500_and_logs_the_detail()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<UnhandledExceptionHandler>();
        var handler = new UnhandledExceptionHandler(logger);

        var httpContext = new DefaultHttpContext
        {
            // ProblemHttpResult.ExecuteAsync resolves services from here; bare = ArgumentNullException.
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        httpContext.Response.Body = new MemoryStream();

        const string Secret =
            "23503: insert or update on table \"journal_lines\" violates foreign key constraint \"fk_journal_lines_entry\"";

        var handled = await handler.TryHandleAsync(
            httpContext, new InvalidOperationException(Secret), ct);

        handled.ShouldBeTrue();
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);

        httpContext.Response.Body.Position = 0;
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync(ct);

        // Nothing internal crosses the wire.
        body.ShouldNotContain("journal_lines");
        body.ShouldNotContain("fk_journal_lines_entry");
        body.ShouldNotContain("23503");
        body.ShouldNotContain("InvalidOperationException");

        // But the user gets something actionable.
        body.ShouldContain("internal_error");
        body.ShouldContain("correlationId");

        // And the engineer gets everything, at Error, with the exception attached.
        var logged = logger.Entries.ShouldHaveSingleItem();
        logged.Level.ShouldBe(LogLevel.Error);
        logged.EventId.ShouldBe(LogEvents.UnhandledException);
        logged.Exception!.Message.ShouldBe(Secret);
    }
}
