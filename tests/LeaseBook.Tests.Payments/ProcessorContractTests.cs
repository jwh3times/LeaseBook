using System.Text.Json;
using LeaseBook.Modules.Payments.Processing;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// The small types every processor adapter speaks through (ADR-046, ADR-054): what a binding collects
/// with when nothing names a test method, how a notice that is not ours differs from one that cannot
/// be read, and what an unanswered question to the processor may say of itself where it is logged.
/// </summary>
public sealed class ProcessorContractTests
{
    private static readonly FixtureBinding Binding = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "acct_contract");

    [Fact]
    public void A_binding_that_names_nothing_more_is_a_simulation_and_collects_with_test_methods_that_succeed()
    {
        // A sandbox host collects with these: the processor's documented test methods for a card and
        // a bank debit that go through.
        (Binding.Mode, Binding.CardPaymentMethod, Binding.AchPaymentMethod)
            .ShouldBe((PaymentModes.Simulation, "pm_card_visa", "pm_usBankAccount_success"));
    }

    [Fact]
    public void A_binding_written_to_a_manifest_carries_its_identity_and_neither_its_mode_nor_its_test_methods()
    {
        var sandbox = Binding with { Mode = PaymentModes.StripeSandbox, CardPaymentMethod = "pm_card_mastercard" };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(sandbox));

        json.RootElement.EnumerateObject().Select(x => x.Name).ShouldBe(["OrgId", "Generation", "BankId", "Account"]);
        var read = JsonSerializer.Deserialize<FixtureBinding>(json.RootElement.GetRawText())!;
        read.ShouldBe(Binding);
    }

    [Fact]
    public void A_notice_that_is_not_ours_is_acknowledged_and_one_that_cannot_be_read_is_refused()
    {
        var notOurs = ProcessorRead<ProcessorNotice>.NotOurs;
        (notOurs.Value, notOurs.Ignored).ShouldBe((null, true));

        var unreadable = ProcessorRead<ProcessorNotice>.Of(null);
        (unreadable.Value, unreadable.Ignored).ShouldBe((null, false));
        unreadable.ShouldNotBe(notOurs);

        var notice = new ProcessorNotice([1, 2, 3]);
        var read = ProcessorRead<ProcessorNotice>.Of(notice);
        (read.Value, read.Ignored).ShouldBe((notice, false));
    }

    [Fact]
    public void A_condition_the_adapter_found_is_its_own_sentence_and_is_kept_as_the_condition()
    {
        const string Condition = "The processor did not answer with the object asked for.";

        var unanswered = new ProcessorUnansweredException(Condition);

        unanswered.ShouldBeAssignableTo<IOException>();
        (unanswered.Message, unanswered.Condition, unanswered.InnerException).ShouldBe((Condition, Condition, null));
    }

    [Fact]
    public void A_failure_of_the_processors_library_keeps_its_text_as_the_cause_and_never_as_the_message()
    {
        var cause = new InvalidOperationException("Invalid API key provided: sk_test_****");

        var unanswered = new ProcessorUnansweredException(cause);

        // The message is LeaseBook's own and constant, so a log that prints it repeats nothing the library said.
        unanswered.Message.ShouldBe("The processor could not be asked.");
        unanswered.InnerException.ShouldBeSameAs(cause);
        unanswered.Condition.ShouldBeNull();
    }
}
