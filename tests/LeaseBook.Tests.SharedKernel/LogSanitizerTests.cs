using LeaseBook.SharedKernel;
using Shouldly;
using Xunit;

namespace LeaseBook.Tests.SharedKernel;

public sealed class LogSanitizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("owner@example.com", "owner@example.com")]
    public void Returns_null_empty_or_clean_input(string? input, string expected) =>
        LogSanitizer.Clean(input).ShouldBe(expected);

    [Fact]
    public void Strips_crlf_so_a_crafted_value_cannot_forge_log_lines()
    {
        var forged = "ok@example.com\r\nWARN  Statement delivery blocked: forged entry";

        var cleaned = LogSanitizer.Clean(forged);

        cleaned.ShouldNotContain("\r");
        cleaned.ShouldNotContain("\n");
        cleaned.ShouldBe("ok@example.comWARN  Statement delivery blocked: forged entry");
    }

    [Fact]
    public void Strips_other_control_characters_including_tab_and_null() =>
        LogSanitizer.Clean("a\tb\0c").ShouldBe("abc");

    [Fact]
    public void Keeps_non_control_whitespace_like_spaces() =>
        LogSanitizer.Clean("a b c").ShouldBe("a b c");
}
