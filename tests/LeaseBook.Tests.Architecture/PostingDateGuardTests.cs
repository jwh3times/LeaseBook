using System.Reflection;
using LeaseBook.Modules.Accounting;
using LeaseBook.SharedKernel.Cqrs;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// Money moving in or out cannot be dated after the organization's today (#642, ADR-037). The guard is a
/// call each handler makes, so a new dated command that forgot it would post a future date with every
/// test green. This reads the compiled handlers: every Accounting command that carries a date either
/// calls <c>PostingCalendar.EnsureNotAfterTodayAsync</c> or is listed below with the reason it may not.
/// </summary>
public sealed class PostingDateGuardTests
{
    private const string Guard = "PostingCalendar::EnsureNotAfterTodayAsync";

    /// <summary>Dated commands that are not money moving in or out on a day staff choose.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["AddCharge"] = "a charge may be billed ahead of the day it falls due",
        ["IssueCredit"] = "a credit may be given against a charge billed ahead",
        ["PostPaymentSettlement"] = "dated by the bank's payout evidence, under the processor's own rules",
        ["ReturnTenantPayment"] = "dated by the bank's return evidence, never a date staff type in",
    };

    [Fact]
    public void Every_dated_command_calls_the_guard_or_says_why_not()
    {
        var unguarded = DatedHandlers()
            .Where(handler => !CallsGuard(handler.Handler) && !Exempt.ContainsKey(handler.Command.Name))
            .Select(handler => handler.Command.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        unguarded.ShouldBeEmpty(
            "a dated Accounting command neither refuses a date after the organization's today nor is " +
            "exempt; call PostingCalendar.EnsureNotAfterTodayAsync in its handler, or list it in Exempt with why");
    }

    [Fact]
    public void Every_exemption_is_a_dated_command_that_really_skips_the_guard()
    {
        var dated = DatedHandlers().ToDictionary(handler => handler.Command.Name, handler => handler.Handler);

        var stale = Exempt.Keys
            .Where(name => !dated.TryGetValue(name, out var handler) || CallsGuard(handler))
            .Order(StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty("remove exemptions for commands that are gone, undated, or now guarded");
    }

    private static bool CallsGuard(Type handler) =>
        CompiledCode.In(handler).Any(reference =>
            reference.Kind == CompiledReferenceKind.Method && reference.Target.Contains(Guard, StringComparison.Ordinal));

    private static IEnumerable<(Type Command, Type Handler)> DatedHandlers() =>
        typeof(AccountingModuleServiceCollectionExtensions).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>))
                .Select(i => (Command: i.GetGenericArguments()[0], Handler: type)))
            .Where(pair => pair.Command.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.PropertyType == typeof(DateOnly) || property.PropertyType == typeof(DateOnly?)));
}
