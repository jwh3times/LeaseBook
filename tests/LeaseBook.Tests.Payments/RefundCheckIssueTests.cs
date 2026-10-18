using System.Collections.Concurrent;
using System.Diagnostics;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.SharedKernel.Observability;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Issuing a refund check (#473): the check is drawn on the bank that holds the tenant's money, which
/// Accounting decides; its number is free on that bank; a retried request returns the check it already
/// made; and the same key carrying anything different is refused. Each test runs in an organization of
/// its own, over the host's ledger adapter and real Accounting (see <see cref="RefundCheckHarness"/>).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RefundCheckIssueTests(PostgresFixture fixture)
{
    private static async Task<decimal> HeldAsync(RefundCheckHarness h, string source, CancellationToken ct, int tenant = 0) =>
        (await h.QueryAsync(new GetRefundCheckOptions(h.Tenants[tenant]), ct)).Funds
            .Where(f => f.Source == source).Sum(f => f.Held);

    private static async Task<int> ChecksAsync(RefundCheckHarness h, CancellationToken ct) =>
        (await h.QueryAsync(new GetRefundChecks(), ct)).Total;

    private static async Task<RefundCheckConflictException> RefusedAsync(RefundCheckHarness h, IssueRefundCheck request,
        CancellationToken ct) =>
        await Should.ThrowAsync<RefundCheckConflictException>(() => h.SendAsync(request, ct));

    [Fact]
    public async Task A_deposit_refund_is_drawn_on_the_deposit_bank_and_reads_back_as_issued()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);

        var issued = await h.SendAsync(h.Issue(1043, 1450m), ct);

        (issued.TenantId, issued.BankAccountId, issued.CheckNumber, issued.Source, issued.Amount, issued.IssueDate)
            .ShouldBe((h.Tenants[0], h.DepositBankId, 1043, "deposit", 1450m, RefundCheckHarness.Issued));
        (issued.PayeeName, issued.AddressLine1, issued.AddressLine2, issued.City, issued.State, issued.PostalCode, issued.Memo)
            .ShouldBe(("Jasmine Carter", "412 Oakmont Ave", "Apt 2B", "Asheville", "NC", "28801", "Deposit refund"));
        (issued.Status, issued.VoidEntryId, issued.PrintCount, issued.LastPrintedAt).ShouldBe(("outstanding", null, 0, null));

        // What was recorded is what came back, and the held deposit is spent.
        var stored = (await h.QueryAsync(new GetRefundChecks(Id: issued.Id), ct)).Items.ShouldHaveSingleItem();
        stored.ShouldBe(issued with { CreatedAt = stored.CreatedAt });
        (await HeldAsync(h, "deposit", ct)).ShouldBe(0m);
    }

    [Fact]
    public async Task A_refund_larger_than_the_held_balance_is_refused_and_records_no_check()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);

        await Should.ThrowAsync<InsufficientLiabilityException>(() => h.SendAsync(h.Issue(1043, 1450.01m), ct));
        await Should.ThrowAsync<InsufficientLiabilityException>(() => h.SendAsync(h.Issue(1043, 10m, "prepayment"), ct));

        (await ChecksAsync(h, ct)).ShouldBe(0);
        (await HeldAsync(h, "deposit", ct)).ShouldBe(1450m);
    }

    [Fact]
    public async Task A_number_is_used_once_per_bank_and_is_still_free_on_another()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        await h.HoldPrepaymentAsync(75m, ct);

        var first = await h.SendAsync(h.Issue(1001), ct);
        var next = await h.SendAsync(h.Issue(1002), ct);
        var otherBank = await h.SendAsync(h.Issue(1001, 75m, "prepayment"), ct);

        (first.BankAccountId, next.BankAccountId, otherBank.BankAccountId).ShouldBe((h.DepositBankId, h.DepositBankId, h.TrustBankId));
        var taken = await RefusedAsync(h, h.Issue(1001), ct);
        (taken.Code, taken.Message).ShouldBe(("check_number_taken", "That check number has already been used on this bank account."));

        // The refused check's refund was posted before the number was found taken; it is rolled back with it.
        (await ChecksAsync(h, ct)).ShouldBe(3);
        (await HeldAsync(h, "deposit", ct)).ShouldBe(1250m);
    }

    [Fact]
    public async Task A_retried_issue_returns_the_original_check_and_posts_nothing_more()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        var request = h.Issue(1043, 1450m);
        var issued = await h.SendAsync(request, ct);

        // A form resent with stray spaces is the same request: names and the address are trimmed when stored.
        var retried = await h.SendAsync(request with
        {
            PayeeName = "  Jasmine Carter ",
            AddressLine1 = " 412 Oakmont Ave",
            AddressLine2 = "Apt 2B  ",
            City = "Asheville ",
            Memo = " Deposit refund",
        }, ct);

        retried.ShouldBe(issued with { CreatedAt = retried.CreatedAt });
        (await ChecksAsync(h, ct)).ShouldBe(1);
        (await HeldAsync(h, "deposit", ct)).ShouldBe(0m);
    }

    [Fact]
    public async Task Blank_optional_fields_are_stored_as_none_and_a_retry_may_leave_them_out()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        var request = h.Issue(1043) with { AddressLine2 = "   ", Memo = " " };

        var issued = await h.SendAsync(request, ct);
        var retried = await h.SendAsync(request with { AddressLine2 = null, Memo = null }, ct);

        (issued.AddressLine2, issued.Memo).ShouldBe((null, null));
        retried.Id.ShouldBe(issued.Id);
    }

    [Fact]
    public async Task The_same_key_with_any_field_changed_is_a_conflict_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        await h.HoldDepositAsync(1450m, ct, tenant: 1);
        await h.HoldPrepaymentAsync(75m, ct);
        var request = h.Issue(1043);
        var issued = await h.SendAsync(request, ct);

        IssueRefundCheck[] changed =
        [
            request with { TenantId = h.Tenants[1] },
            request with { Source = "prepayment" },
            request with { Amount = 100.01m },
            request with { CheckNumber = 1044 },
            request with { Date = RefundCheckHarness.Issued.AddDays(1) },
            request with { PayeeName = "Jasmine Carter-Ruiz" },
            request with { AddressLine1 = "414 Oakmont Ave" },
            request with { AddressLine2 = "Apt 3C" },
            request with { AddressLine2 = null },
            request with { City = "Weaverville" },
            request with { State = "SC" },
            request with { PostalCode = "28787" },
            request with { Memo = "Final refund" },
            request with { Memo = null },
            request with { Bucket = new RefundFundsBucket(h.TrustBankId, null, null) },
        ];
        foreach (var conflicting in changed)
        {
            var refused = await RefusedAsync(h, conflicting, ct);
            (refused.Code, refused.Message).ShouldBe(("refund_check_conflict", "This request conflicts with a check that was already issued."));
        }

        // Naming the bucket the check was drawn on changes nothing.
        var named = await h.SendAsync(request with { Bucket = new RefundFundsBucket(h.DepositBankId, h.PropertyId, h.OwnerId) }, ct);
        named.Id.ShouldBe(issued.Id);
        (await ChecksAsync(h, ct)).ShouldBe(1);
        (await HeldAsync(h, "deposit", ct)).ShouldBe(1350m);
        (await HeldAsync(h, "deposit", ct, tenant: 1)).ShouldBe(1450m);
    }

    [Fact]
    public async Task Prepaid_credit_held_in_two_banks_is_drawn_on_the_bucket_named()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldPrepaymentAsync(75m, ct);
        await h.HoldPrepaymentAsync(40m, ct, bank: h.DepositBankId);

        await Should.ThrowAsync<RefundBucketAmbiguousException>(() => h.SendAsync(h.Issue(2001, 40m, "prepayment"), ct));
        var issued = await h.SendAsync(h.Issue(2001, 40m, "prepayment") with
        {
            Bucket = new RefundFundsBucket(h.DepositBankId, null, null),
        }, ct);

        issued.BankAccountId.ShouldBe(h.DepositBankId);
        (await HeldAsync(h, "prepayment", ct)).ShouldBe(75m);
    }

    [Fact]
    public async Task The_internal_note_reaches_the_ledger_trimmed_and_a_blank_one_is_none()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);

        var noted = await h.SendAsync(h.Issue(1001) with { InternalNote = "  Forwarding address from the move-out form " }, ct);
        var blank = await h.SendAsync(h.Issue(1002) with { InternalNote = "   " }, ct);
        var none = await h.SendAsync(h.Issue(1003), ct);

        var notes = await h.QueryAsync(new GetEntryInternalNotes([noted.EntryId, blank.EntryId, none.EntryId]), ct);
        notes.ShouldBe(new Dictionary<Guid, string> { [noted.EntryId] = "Forwarding address from the move-out form" });
    }

    [Fact]
    public async Task Issuing_and_voiding_leave_a_trace_event_naming_only_the_check_and_the_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        var traced = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LeaseBookTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = traced.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var issued = await h.SendAsync(h.Issue(1043), ct);
        var voided = (await h.SendAsync(new VoidIssuedRefundCheck(issued.Id, "Misprinted"), ct))!;

        // No payee data in telemetry: the ids are enough to find the check.
        Dictionary<string, object?> TagsOf(string name) => traced.SelectMany(a => a.Events)
            .Where(e => e.Name == name && e.Tags.Any(t => t.Key == "check_id" && Equals(t.Value, issued.Id)))
            .ShouldHaveSingleItem().Tags.ToDictionary(t => t.Key, t => t.Value);
        TagsOf("payments.refund_check_issued")
            .ShouldBe(new Dictionary<string, object?> { ["check_id"] = issued.Id, ["entry_id"] = issued.EntryId });
        TagsOf("payments.refund_check_voided")
            .ShouldBe(new Dictionary<string, object?> { ["check_id"] = issued.Id, ["entry_id"] = voided.VoidEntryId });
    }

    [Fact]
    public async Task Refund_check_work_without_an_organization_fails_before_it_reads_anything()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);

        // Outside a unit of work the context carries no organization, as a background job that forgot to set one.
        var issue = await Should.ThrowAsync<InvalidOperationException>(() => h.Sender.Send(h.Issue(1043), ct));
        var print = await Should.ThrowAsync<InvalidOperationException>(() => h.Sender.Send(new RecordRefundCheckPrint(Guid.NewGuid()), ct));

        issue.Message.ShouldBe("Refund checks require an organization context.");
        print.Message.ShouldBe("Refund checks require an organization context.");
    }
}
