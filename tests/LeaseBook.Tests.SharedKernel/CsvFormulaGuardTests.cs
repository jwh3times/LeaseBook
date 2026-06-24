using LeaseBook.SharedKernel.Csv;
using Shouldly;

namespace LeaseBook.Tests.SharedKernel;

/// <summary>
/// CSV formula-injection neutralization (OWASP). A cell whose first character is a spreadsheet
/// formula trigger (<c>= + - @</c>, tab, CR) is prefixed with an apostrophe so the spreadsheet
/// treats it as text — EXCEPT server-formatted signed numbers (e.g. <c>-250.00</c>), which must stay
/// numeric so exported money still sums.
/// </summary>
public sealed class CsvFormulaGuardTests
{
    [Theory]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("+cmd|'/c calc'!A1", "'+cmd|'/c calc'!A1")]
    [InlineData("-2+3+cmd|'/c calc'!A1", "'-2+3+cmd|'/c calc'!A1")]
    [InlineData("\tinjected", "'\tinjected")]
    [InlineData("\rinjected", "'\rinjected")]
    public void Neutralize_prefixes_formula_payloads_with_an_apostrophe(string input, string expected) =>
        CsvFormulaGuard.Neutralize(input).ShouldBe(expected);

    [Theory]
    [InlineData("-250.00")]
    [InlineData("+5.00")]
    [InlineData("1000.00")]
    [InlineData("0.00")]
    [InlineData("-0.01")]
    public void Neutralize_leaves_server_formatted_signed_numbers_untouched(string number) =>
        CsvFormulaGuard.Neutralize(number).ShouldBe(number);

    [Theory]
    [InlineData("Feb rent")]
    [InlineData("204 Elm St")]
    [InlineData("Ridgeline Investments")]
    [InlineData("")]
    public void Neutralize_leaves_safe_text_untouched(string text) =>
        CsvFormulaGuard.Neutralize(text).ShouldBe(text);

    [Fact]
    public void Neutralize_maps_null_to_empty_string() =>
        CsvFormulaGuard.Neutralize(null).ShouldBe(string.Empty);
}
