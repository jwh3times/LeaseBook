using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Tests.SharedKernel.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace LeaseBook.Tests.SharedKernel;

public sealed class CqrsDispatcherTests
{
    private static ServiceProvider BuildProvider(List<string> trace)
    {
        var services = new ServiceCollection();
        services.AddSingleton(trace);
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(new TraceLoggerProvider(trace));
        });
        services.AddLeaseBookCqrs(typeof(PingCommand).Assembly);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Dispatcher_resolves_the_right_command_and_query_handlers()
    {
        var trace = new List<string>();
        await using var provider = BuildProvider(trace);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var ct = TestContext.Current.CancellationToken;
        (await sender.Send(new PingCommand("hi"), ct)).ShouldBe("handled:hi");
        (await sender.Query(new PingQuery("yo"), ct)).ShouldBe("queried:yo");
    }

    [Fact]
    public async Task Pipeline_order_is_telemetry_then_validation_then_handler()
    {
        var trace = new List<string>();
        await using var provider = BuildProvider(trace);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.Send(new TracedCommand(), TestContext.Current.CancellationToken);

        result.ShouldBe(42);
        trace[0].ShouldContain("Dispatching", Case.Sensitive); // telemetry runs first (outermost)
        var validateIndex = trace.IndexOf("validate");
        var handlerIndex = trace.IndexOf("handler");
        validateIndex.ShouldBeGreaterThan(0);                  // after telemetry start
        handlerIndex.ShouldBeGreaterThan(validateIndex);       // handler runs after validation
        trace[^1].ShouldContain("completed");                  // telemetry closes the dispatch
    }

    [Fact]
    public async Task Validation_failure_short_circuits_with_the_failure_set()
    {
        var trace = new List<string>();
        await using var provider = BuildProvider(trace);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var ex = await Should.ThrowAsync<ValidationException>(
            async () => await sender.Send(new GuardedCommand(0), TestContext.Current.CancellationToken));

        ex.Errors.ShouldContain(f => f.ErrorMessage == "Value must be positive.");
        trace.ShouldNotContain("handler");                       // handler never ran
        trace.ShouldContain(e => e.Contains("Dispatching"));     // telemetry observed the dispatch
        trace.ShouldContain(e => e.Contains("failed"));          // telemetry (outermost) saw the failure
    }

    [Fact]
    public async Task Unknown_message_throws_a_clear_error()
    {
        var trace = new List<string>();
        await using var provider = BuildProvider(trace);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await sender.Send(new OrphanCommand(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("OrphanCommand");
        ex.Message.ShouldContain("No command handler registered");
    }
}
