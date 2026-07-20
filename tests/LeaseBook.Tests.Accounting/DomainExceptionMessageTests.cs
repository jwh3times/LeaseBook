using System.Reflection;
using System.Text.RegularExpressions;
using LeaseBook.Modules.Accounting.Contracts;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The enforcement behind ADR-025's error-content rule: a user-facing message may carry money
/// amounts, dates, periods and names — never identifiers, account codes, snake_case database
/// identifiers, or C# type names. Without this test the rule is only a comment, and the next
/// message written re-introduces the leak.
/// </summary>
public sealed class DomainExceptionMessageTests
{
    private static readonly Guid SampleId = Guid.Parse("00000000-0000-0000-0000-00000badbeef");
    private const string SampleCode = "SAMPLE_ACCOUNT_CODE";

    [Fact]
    public void No_domain_exception_message_leaks_an_identifier_or_internal_name()
    {
        var offenders = new List<string>();

        foreach (var exception in ConstructEveryDomainException())
        {
            var message = exception.Message;
            var type = exception.GetType().Name;

            if (message.Contains(SampleId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"{type}: leaks a GUID → {message}");
            }

            if (message.Contains(SampleCode, StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"{type}: leaks an account code → {message}");
            }

            // snake_case identifiers are database/column names (pm_income, owner_id, source_ref).
            if (Regex.IsMatch(message, @"\b[a-z]{2,}_[a-z_]{2,}\b"))
            {
                offenders.Add($"{type}: leaks a snake_case identifier → {message}");
            }

            // C# type names leaking into user copy.
            if (Regex.IsMatch(message, @"\b\w*(Exception|Service|Handler|Strategy)\b"))
            {
                offenders.Add($"{type}: leaks a type name → {message}");
            }
        }

        offenders.ShouldBeEmpty();
    }

    /// <summary>
    /// Reflectively builds one instance per concrete domain exception, iterating every value of a
    /// discriminator enum so each message branch is covered.
    /// </summary>
    private static IEnumerable<AccountingDomainException> ConstructEveryDomainException()
    {
        var types = typeof(AccountingDomainException).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(AccountingDomainException).IsAssignableFrom(t))
            .OrderBy(t => t.Name);

        foreach (var type in types)
        {
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            var parameters = ctor.GetParameters();
            var enumIndex = Array.FindIndex(parameters, p => Underlying(p.ParameterType).IsEnum);

            if (enumIndex < 0)
            {
                yield return (AccountingDomainException)ctor.Invoke(
                    [.. parameters.Select(p => SampleValue(p.ParameterType, null))]);
                continue;
            }

            foreach (var enumValue in Enum.GetValues(Underlying(parameters[enumIndex].ParameterType)))
            {
                var args = parameters
                    .Select((p, i) => SampleValue(p.ParameterType, i == enumIndex ? enumValue : null))
                    .ToArray();
                yield return (AccountingDomainException)ctor.Invoke(args);
            }
        }
    }

    private static Type Underlying(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    private static object? SampleValue(Type parameterType, object? enumOverride)
    {
        if (enumOverride is not null)
        {
            return enumOverride;
        }

        var t = Underlying(parameterType);

        if (t.IsEnum) return Enum.GetValues(t).GetValue(0);
        if (t == typeof(Guid)) return SampleId;
        if (t == typeof(decimal)) return 1234.56m;
        if (t == typeof(int)) return 2026;
        if (t == typeof(string)) return SampleCode;

        throw new NotSupportedException(
            $"DomainExceptionMessageTests needs a sample value for {t.Name}. Add one above.");
    }
}
