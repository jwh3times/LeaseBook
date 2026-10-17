using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Submitting a payment (ADR-046, ADR-053): what is stored, what a replay finds, and what is refused
/// before anything is requested of a processor.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PaymentSubmissionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_submission_stores_the_ledger_amount_and_the_quoted_fee_and_is_due_now()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        h.FeeRules.Fee = 2.50m;
        var key = UuidV7.NewId();

        var op = await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, key, 100m, "USD",
            PaymentMethods.Card, 2.50m, ct), ct);

        var stored = await h.ReadAsync(op.Id, ct);
        (stored.TenantId, stored.UserId, stored.Key, stored.Amount, stored.QuotedFee, stored.ChargedAmount)
            .ShouldBe((h.Tenants[0], h.UserId, key, 100m, 2.50m, 102.50m));
        (stored.Method, stored.Currency, stored.Status, stored.Reason, stored.ProviderId, stored.JournalId)
            .ShouldBe((PaymentMethods.Card, "USD", "Requested", (string?)null, (string?)null, (Guid?)null));
        (stored.Generation, stored.BankId, stored.Account).ShouldBe((h.Binding.Generation, h.Binding.BankId, h.Binding.Account));
        stored.DueAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMilliseconds(1));
        stored.Fingerprint.Length.ShouldBe(64);
        h.Eligibility.Asked.ShouldBe([new(h.Tenants[0], h.Binding.BankId, h.Today)]);
    }

    [Fact]
    public async Task A_replay_of_the_same_request_finds_the_first_and_a_different_one_under_its_key_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var key = UuidV7.NewId();
        Task<PaymentOperation> Submit(decimal amount, decimal fee, string method = PaymentMethods.Ach) =>
            h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, key, amount, "USD", method, fee, ct), ct);

        var first = await Submit(100m, 0m);
        // The fee rule changes after the tenant confirmed: a replay still finds the payment it made.
        h.FeeRules.Fee = 9m;
        (await Submit(100m, 0m)).Id.ShouldBe(first.Id);

        await Should.ThrowAsync<PaymentConflictException>(() => Submit(101m, 0m));
        await Should.ThrowAsync<PaymentConflictException>(() => Submit(100m, 9m));
        await Should.ThrowAsync<PaymentConflictException>(() => Submit(100m, 0m, PaymentMethods.Card));
        await Should.ThrowAsync<PaymentConflictException>(() => h.InOrg(() => h.Payments.SubmitAsync(h.Binding,
            h.Tenants[1], h.UserId, key, 100m, "USD", PaymentMethods.Ach, 0m, ct), ct));
        (await h.InOrg(() => h.Scope.Db.Set<PaymentOperation>().CountAsync(ct), ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_replay_finds_its_payment_whatever_scale_the_amount_and_the_fee_are_written_in()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        h.FeeRules.Fee = 2.5m;
        var key = UuidV7.NewId();
        Task<PaymentOperation> Submit(decimal amount, decimal fee) =>
            h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, key, amount, "USD", PaymentMethods.Card, fee, ct), ct);

        var first = await Submit(100m, 2.5m);

        // The same money, written with more or fewer places: the same request.
        (await Submit(100.00m, 2.5m)).Id.ShouldBe(first.Id);
        (await Submit(100m, 2.500m)).Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task The_same_request_under_another_key_is_another_payment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);

        var a = await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, UuidV7.NewId(), 100m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);
        var b = await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, UuidV7.NewId(), 100m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);

        b.Id.ShouldNotBe(a.Id);
        b.Fingerprint.ShouldBe(a.Fingerprint);
    }

    [Fact]
    public async Task A_fee_that_moved_since_the_quote_is_refused_and_nothing_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        h.FeeRules.Fee = 3m;

        await Should.ThrowAsync<PaymentFeeQuoteChangedException>(() => h.InOrg(() => h.Payments.SubmitAsync(h.Binding,
            h.Tenants[0], h.UserId, UuidV7.NewId(), 100m, "USD", PaymentMethods.Ach, 2.99m, ct), ct));

        (await h.InOrg(() => h.Scope.Db.Set<PaymentOperation>().CountAsync(ct), ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_tenant_who_may_not_pay_on_the_fixture_bank_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        h.Eligibility.Eligible = false;

        await Should.ThrowAsync<PaymentUnavailableException>(() => h.InOrg(() => h.Payments.SubmitAsync(h.Binding,
            h.Tenants[0], h.UserId, UuidV7.NewId(), 100m, "USD", PaymentMethods.Ach, 0m, ct), ct));

        (await h.InOrg(() => h.Scope.Db.Set<PaymentOperation>().CountAsync(ct), ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_quote_is_the_organizations_rule_for_the_method_and_an_unknown_method_has_none()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        h.FeeRules.Fee = 1.25m;

        (await h.InOrg(() => h.Payments.QuoteAsync(h.Binding, 40m, PaymentMethods.Card, ct), ct))
            .ShouldBe(new FeeQuote(40m, 1.25m, 41.25m));
        await Should.ThrowAsync<PaymentUnavailableException>(() =>
            h.InOrg(() => h.Payments.QuoteAsync(h.Binding, 40m, "wire", ct), ct));
    }

    public static TheoryData<string> OtherBindings() => ["org", "generation", "bank", "account"];

    [Theory]
    [MemberData(nameof(OtherBindings))]
    public async Task A_binding_that_is_not_the_organizations_fixture_is_refused(string differs)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var other = differs switch
        {
            "org" => h.Binding with { OrgId = UuidV7.NewId() },
            "generation" => h.Binding with { Generation = UuidV7.NewId() },
            "bank" => h.Binding with { BankId = UuidV7.NewId() },
            _ => h.Binding with { Account = "acct_other" },
        };

        await Should.ThrowAsync<PaymentUnavailableException>(() => h.InOrg(() => h.Payments.RequireFixtureAsync(other, ct), ct));
        await h.InOrg(() => h.Payments.RequireFixtureAsync(h.Binding, ct), ct);
    }

    [Fact]
    public async Task Payment_work_outside_an_organization_transaction_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);

        await Should.ThrowAsync<PaymentUnavailableException>(() => h.Payments.RequireFixtureAsync(h.Binding, ct));
        // A programming fault, which reaches the host's log of unhandled exceptions with its message.
        (await Should.ThrowAsync<InvalidOperationException>(() => h.Payments.LockAsync("anything", ct)))
            .Message.ShouldBe("Payment work requires an organization transaction.");
        // A transaction with no organization in it is no better.
        await using (await h.Scope.Db.Database.BeginTransactionAsync(ct))
        {
            await Should.ThrowAsync<InvalidOperationException>(() => h.Payments.LockAsync("anything", ct));
        }
    }

    [Fact]
    public async Task A_quote_names_the_method_the_amount_for_the_ledger_the_fee_on_top_and_the_total_charged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        h.FeeRules.Fee = 2.50m;

        var quote = await h.QueryAsync(new GetPaymentQuote(h.Binding, 100m, PaymentMethods.Card), ct);

        quote.ShouldBe(new PaymentQuoteView(PaymentMethods.Card, 100m, 2.50m, 102.50m));
        // An amount no payment could be made for is refused before any rule is read.
        await Should.ThrowAsync<FluentValidation.ValidationException>(() =>
            h.QueryAsync(new GetPaymentQuote(h.Binding, 0m, PaymentMethods.Card), ct));
    }

    [Fact]
    public async Task A_submission_answers_with_the_tenants_view_of_the_payment_it_requested()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        h.FeeRules.Fee = 2.50m;

        var view = await h.SendAsync(new SubmitPayment(h.Binding, h.Tenants[0], h.UserId, UuidV7.NewId(), 100m, "USD",
            PaymentMethods.Card, 2.50m), ct);

        var stored = await h.ReadAsync(view.Id, ct);
        (view.Status, view.Amount, view.QuotedFee, view.ChargedAmount, view.Method, view.Currency)
            .ShouldBe(("Requested", 100m, 2.50m, 102.50m, PaymentMethods.Card, "USD"));
        (view.ReceiptRecorded, view.CanRetry, view.Reason, view.EvidenceReference)
            .ShouldBe((false, false, (string?)null, (string?)null));
        // The database keeps whole microseconds of the time the request was stored.
        view.CreatedAt.ShouldBe(stored.CreatedAt, TimeSpan.FromMilliseconds(1));
        (stored.TenantId, stored.UserId).ShouldBe((h.Tenants[0], h.UserId));
    }

    [Fact]
    public async Task A_submission_outside_the_rules_is_refused_before_anything_is_stored_or_asked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);

        var refused = await Should.ThrowAsync<FluentValidation.ValidationException>(() => h.SendAsync(new SubmitPayment(h.Binding,
            h.Tenants[0], h.UserId, UuidV7.NewId(), 100m, "EUR"), ct));

        refused.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Currency");
        (await h.InOrg(() => h.Scope.Db.Set<PaymentOperation>().CountAsync(ct), ct)).ShouldBe(0);
        h.Eligibility.Asked.ShouldBeEmpty();
    }
}
