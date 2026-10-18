using System.Globalization;
using FluentValidation;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Features.RefundChecks;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// What the refund-check commands and queries refuse before a handler runs (#473, #474, #476). Each case
/// changes one field of a request that passes and names the field the 400 points at, so the form can put
/// the message beside it; each boundary is tried from both sides.
/// </summary>
public sealed class RefundCheckValidatorTests
{
    private static readonly IssueRefundCheck Issue = new(
        Guid.NewGuid(), Guid.NewGuid(), "deposit", 1450m, new DateOnly(2026, 3, 2), 1043,
        "Jasmine Carter", "412 Oakmont Ave", null, "Asheville", "NC", "28801", null, null);

    private static readonly SaveBankMicrDetails Micr = new(Guid.NewGuid(), "blank", "111000012", "123456789U", 0m, 0m);

    private static readonly SaveCheckPrintSettings Offsets = new(Guid.NewGuid(), 0m, 0m);

    private static string[] Refused<T>(AbstractValidator<T> validator, T request) =>
        [.. validator.Validate(request).Errors.Select(e => e.PropertyName).Distinct()];

    private static string MessageFor<T>(AbstractValidator<T> validator, T request) =>
        validator.Validate(request).Errors.ShouldHaveSingleItem().ErrorMessage;

    public static TheoryData<string, IssueRefundCheck> AcceptedIssues => new()
    {
        { "as it stands", Issue },
        { "a prepayment", Issue with { Source = "prepayment" } },
        { "one cent", Issue with { Amount = 0.01m } },
        { "the ceiling", Issue with { Amount = 1_000_000m } },
        { "the first check number", Issue with { CheckNumber = 1 } },
        { "the last check number", Issue with { CheckNumber = 99_999_999 } },
        { "the longest payee", Issue with { PayeeName = new string('a', 120) } },
        { "the longest street line", Issue with { AddressLine1 = new string('a', 120) } },
        { "the longest second line", Issue with { AddressLine2 = new string('a', 120) } },
        { "the longest city", Issue with { City = new string('a', 60) } },
        { "ZIP+4", Issue with { PostalCode = "28801-1234" } },
        { "the longest memo", Issue with { Memo = new string('a', 60) } },
        { "the longest note", Issue with { InternalNote = new string('a', 500) } },
        { "a named bucket", Issue with { Bucket = new RefundFundsBucket(Guid.NewGuid(), null, null) } },
    };

    [Theory]
    [MemberData(nameof(AcceptedIssues))]
    public void An_issue_within_every_limit_passes(string _, IssueRefundCheck request) =>
        Refused(new IssueRefundCheckValidator(), request).ShouldBeEmpty();

    public static TheoryData<string, IssueRefundCheck, string> RefusedIssues => new()
    {
        { "no key", Issue with { Key = Guid.Empty }, "Key" },
        { "no tenant", Issue with { TenantId = Guid.Empty }, "TenantId" },
        { "an unknown source", Issue with { Source = "rent" }, "Source" },
        { "nothing", Issue with { Amount = 0m }, "Amount" },
        { "a negative amount", Issue with { Amount = -5m }, "Amount" },
        { "a cent over the ceiling", Issue with { Amount = 1_000_000.01m }, "Amount" },
        { "a part of a cent", Issue with { Amount = 1.005m }, "Amount" },
        { "check number zero", Issue with { CheckNumber = 0 }, "CheckNumber" },
        { "a nine-digit check number", Issue with { CheckNumber = 100_000_000 }, "CheckNumber" },
        { "no payee", Issue with { PayeeName = "" }, "PayeeName" },
        { "a payee too long", Issue with { PayeeName = new string('a', 121) }, "PayeeName" },
        { "no street line", Issue with { AddressLine1 = "" }, "AddressLine1" },
        { "a street line too long", Issue with { AddressLine1 = new string('a', 121) }, "AddressLine1" },
        { "a second line too long", Issue with { AddressLine2 = new string('a', 121) }, "AddressLine2" },
        { "no city", Issue with { City = "" }, "City" },
        { "a city too long", Issue with { City = new string('a', 61) }, "City" },
        { "a lower-case state", Issue with { State = "nc" }, "State" },
        { "a state name", Issue with { State = "North Carolina" }, "State" },
        { "a four-digit ZIP", Issue with { PostalCode = "2880" }, "PostalCode" },
        { "a short ZIP+4", Issue with { PostalCode = "28801-12" }, "PostalCode" },
        { "a memo too long", Issue with { Memo = new string('a', 61) }, "Memo" },
        { "a note too long", Issue with { InternalNote = new string('a', 501) }, "InternalNote" },
        { "a bucket with no bank", Issue with { Bucket = new RefundFundsBucket(Guid.Empty, null, null) }, "Bucket.BankAccountId" },
    };

    [Theory]
    [MemberData(nameof(RefusedIssues))]
    public void An_issue_outside_a_limit_is_refused_on_that_field(string _, IssueRefundCheck request, string field) =>
        Refused(new IssueRefundCheckValidator(), request).ShouldBe([field]);

    [Fact]
    public void An_issue_s_messages_say_what_the_field_takes()
    {
        var validator = new IssueRefundCheckValidator();

        MessageFor(validator, Issue with { Source = "rent" }).ShouldBe("Source must be deposit or prepayment.");
        MessageFor(validator, Issue with { Amount = 0m })
            .ShouldBe("Amount must be a positive value up to 1,000,000.00 with at most 2 decimal places.");
        MessageFor(validator, Issue with { State = "nc" }).ShouldBe("Use the two-letter state code.");
        MessageFor(validator, Issue with { PostalCode = "2880" }).ShouldBe("Use a 5-digit or ZIP+4 postal code.");
    }

    [Theory]
    [InlineData("Misprinted", true)]
    [InlineData("", false)]
    public void A_void_needs_a_reason(string reason, bool valid) =>
        new VoidIssuedRefundCheckValidator().Validate(new VoidIssuedRefundCheck(Guid.NewGuid(), reason)).IsValid.ShouldBe(valid);

    [Fact]
    public void A_void_s_reason_and_check_are_bounded()
    {
        var validator = new VoidIssuedRefundCheckValidator();

        Refused(validator, new VoidIssuedRefundCheck(Guid.NewGuid(), new string('a', 500))).ShouldBeEmpty();
        Refused(validator, new VoidIssuedRefundCheck(Guid.NewGuid(), new string('a', 501))).ShouldBe(["Reason"]);
        Refused(validator, new VoidIssuedRefundCheck(Guid.Empty, "Misprinted")).ShouldBe(["Id"]);
    }

    [Fact]
    public void A_list_takes_pages_of_one_to_two_hundred_and_only_the_outstanding_filter()
    {
        var validator = new GetRefundChecksValidator();

        Refused(validator, new GetRefundChecks()).ShouldBeEmpty();
        Refused(validator, new GetRefundChecks(Status: "outstanding", Page: 3, PageSize: 1)).ShouldBeEmpty();
        Refused(validator, new GetRefundChecks(PageSize: 200)).ShouldBeEmpty();
        Refused(validator, new GetRefundChecks(Page: 0)).ShouldBe(["Page"]);
        Refused(validator, new GetRefundChecks(PageSize: 0)).ShouldBe(["PageSize"]);
        Refused(validator, new GetRefundChecks(PageSize: 201)).ShouldBe(["PageSize"]);
        Refused(validator, new GetRefundChecks(Status: "cleared")).ShouldBe(["Status"]);
        MessageFor(validator, new GetRefundChecks(Status: "voided")).ShouldBe("Status must be 'outstanding' or omitted.");
    }

    public static TheoryData<string, SaveBankMicrDetails> AcceptedMicr => new()
    {
        { "blank stock with both numbers", Micr },
        { "pre-printed stock with no numbers", Micr with { StockKind = "preprinted", RoutingNumber = null, OnUsAccountNumber = null } },
        { "the furthest left and down", Micr with { MicrOffsetXPoints = -18m, MicrOffsetYPoints = -18m } },
        { "the furthest right and up", Micr with { MicrOffsetXPoints = 18m, MicrOffsetYPoints = 18m } },
        { "hundredths of a point", Micr with { MicrOffsetXPoints = 1.25m, MicrOffsetYPoints = -0.75m } },
    };

    [Theory]
    [MemberData(nameof(AcceptedMicr))]
    public void Micr_details_within_every_limit_pass(string _, SaveBankMicrDetails request) =>
        Refused(new SaveBankMicrDetailsValidator(), request).ShouldBeEmpty();

    public static TheoryData<string, SaveBankMicrDetails, string> RefusedMicr => new()
    {
        { "no bank", Micr with { BankAccountId = Guid.Empty }, "BankAccountId" },
        { "an unknown stock", Micr with { StockKind = "laser" }, "StockKind" },
        { "a wrong check digit", Micr with { RoutingNumber = "111000013" }, "RoutingNumber" },
        { "an On-Us field with a letter", Micr with { OnUsAccountNumber = "1234A" }, "OnUsAccountNumber" },
        { "past the left limit", Micr with { MicrOffsetXPoints = -18.01m }, "MicrOffsetXPoints" },
        { "past the right limit", Micr with { MicrOffsetXPoints = 18.01m }, "MicrOffsetXPoints" },
        { "a thousandth across", Micr with { MicrOffsetXPoints = 0.125m }, "MicrOffsetXPoints" },
        { "past the lower limit", Micr with { MicrOffsetYPoints = -18.01m }, "MicrOffsetYPoints" },
        { "past the upper limit", Micr with { MicrOffsetYPoints = 18.01m }, "MicrOffsetYPoints" },
        { "a thousandth down", Micr with { MicrOffsetYPoints = 0.125m }, "MicrOffsetYPoints" },
    };

    [Theory]
    [MemberData(nameof(RefusedMicr))]
    public void Micr_details_outside_a_limit_are_refused_on_that_field(string _, SaveBankMicrDetails request, string field) =>
        Refused(new SaveBankMicrDetailsValidator(), request).ShouldBe([field]);

    [Fact]
    public void Micr_messages_say_what_the_field_takes()
    {
        var validator = new SaveBankMicrDetailsValidator();

        MessageFor(validator, Micr with { StockKind = "laser" }).ShouldBe("Stock kind must be 'preprinted' or 'blank'.");
        MessageFor(validator, Micr with { RoutingNumber = "111000013" })
            .ShouldBe("The routing number must be nine digits with a valid check digit.");
        MessageFor(validator, Micr with { OnUsAccountNumber = "12U-" })
            .ShouldBe("The On-Us field must be at most 18 digits, dashes, spaces or 'U' symbols, with at least four digits.");
    }

    [Theory]
    [InlineData("0", "0", true)]
    [InlineData("-72", "72", true)]
    [InlineData("72", "-72", true)]
    [InlineData("1.25", "-0.5", true)]
    [InlineData("-72.01", "0", false)]
    [InlineData("72.01", "0", false)]
    [InlineData("0.125", "0", false)]
    [InlineData("0", "-72.01", false)]
    [InlineData("0", "72.01", false)]
    [InlineData("0", "0.125", false)]
    public void Print_offsets_move_at_most_an_inch_in_hundredths_of_a_point(string x, string y, bool valid) =>
        new SaveCheckPrintSettingsValidator().Validate(Offsets with
        {
            OffsetXPoints = decimal.Parse(x, CultureInfo.InvariantCulture),
            OffsetYPoints = decimal.Parse(y, CultureInfo.InvariantCulture),
        }).IsValid.ShouldBe(valid);

    [Fact]
    public void Blank_stock_missing_its_names_is_refused_with_a_code_and_what_to_do()
    {
        // Thrown by the host's print route; the code is what the screen switches on, the message what it shows.
        var refused = RefundCheckConflictException.BlankStockIncomplete();

        refused.Code.ShouldBe("blank_stock_incomplete");
        refused.Message.ShouldBe("Checks on blank stock print the organization's legal name and the bank's name. " +
            "Add the legal name in organization settings and the bank's name on this bank account, then print again.");
    }

    [Fact]
    public void Print_offsets_need_a_bank() =>
        Refused(new SaveCheckPrintSettingsValidator(), Offsets with { BankAccountId = Guid.Empty }).ShouldBe(["BankAccountId"]);
}
