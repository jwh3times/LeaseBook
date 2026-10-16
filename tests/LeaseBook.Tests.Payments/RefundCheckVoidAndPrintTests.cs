using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Voiding and printing a refund check (#473). A void is Accounting's guarded reversal, refused once the
/// bank has paid the check, and it gives the tenant's money back to the bucket it came from; the check
/// record itself never changes, and reads back as voided from the ledger. Every print is counted, and a
/// voided check cannot be printed.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RefundCheckVoidAndPrintTests(PostgresFixture fixture)
{
    private static async Task<RefundCheckView> ReadAsync(RefundCheckHarness h, Guid id, CancellationToken ct) =>
        (await h.QueryAsync(new GetRefundChecks(Id: id), ct)).Items.ShouldHaveSingleItem();

    private static async Task<decimal> HeldDepositAsync(RefundCheckHarness h, CancellationToken ct) =>
        (await h.QueryAsync(new GetRefundCheckOptions(h.Tenants[0]), ct)).Funds.Where(f => f.Source == "deposit").Sum(f => f.Held);

    [Fact]
    public async Task A_voided_check_gives_the_deposit_back_reads_as_voided_and_cannot_be_printed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        var issued = await h.SendAsync(h.Issue(1043, 1450m), ct);
        (await h.SendAsync(new RecordRefundCheckPrint(issued.Id), ct))!.PrintCount.ShouldBe(1);

        var voided = (await h.SendAsync(new VoidIssuedRefundCheck(issued.Id, "  Misprinted "), ct))!;

        (voided.Id, voided.CheckNumber, voided.Status, voided.PrintCount).ShouldBe((issued.Id, 1043, "voided", 1));
        voided.VoidEntryId.ShouldNotBeNull();
        (await ReadAsync(h, issued.Id, ct)).ShouldBe(voided);
        (await HeldDepositAsync(h, ct)).ShouldBe(1450m);
        // The reason is the staff-only note on the reversal, trimmed.
        (await h.QueryAsync(new GetEntryInternalNotes([voided.VoidEntryId.Value]), ct))[voided.VoidEntryId.Value].ShouldBe("Misprinted");

        var refused = await Should.ThrowAsync<RefundCheckConflictException>(() => h.SendAsync(new RecordRefundCheckPrint(issued.Id), ct));
        (refused.Code, refused.Message).ShouldBe(("refund_check_voided", "This check has been voided and cannot be printed."));
        (await ReadAsync(h, issued.Id, ct)).PrintCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_check_the_bank_has_paid_cannot_be_voided()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        var issued = await h.SendAsync(h.Issue(1043, 1450m), ct);
        await h.ClearAsync(issued.EntryId, ct);

        await Should.ThrowAsync<RefundCheckClearedException>(() => h.SendAsync(new VoidIssuedRefundCheck(issued.Id, "Lost"), ct));

        (await ReadAsync(h, issued.Id, ct)).Status.ShouldBe("cleared");
        (await HeldDepositAsync(h, ct)).ShouldBe(0m);
        // A cleared check can still be reprinted, say for the tenant's records.
        (await h.SendAsync(new RecordRefundCheckPrint(issued.Id), ct))!.PrintCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_check_that_cannot_be_found_is_neither_voided_nor_printed_and_others_are_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        var other = await h.SendAsync(h.Issue(1043), ct);

        (await h.SendAsync(new VoidIssuedRefundCheck(UuidV7.NewId(), "Misprinted"), ct)).ShouldBeNull();
        (await h.SendAsync(new RecordRefundCheckPrint(UuidV7.NewId()), ct)).ShouldBeNull();

        var untouched = await ReadAsync(h, other.Id, ct);
        (untouched.Status, untouched.PrintCount).ShouldBe(("outstanding", 0));
    }

    [Fact]
    public async Task Each_print_is_counted_keeps_the_number_and_the_last_one_is_the_latest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        var issued = await h.SendAsync(h.Issue(1043), ct);

        var first = (await h.SendAsync(new RecordRefundCheckPrint(issued.Id), ct))!;
        var second = (await h.SendAsync(new RecordRefundCheckPrint(issued.Id), ct))!;

        (first.PrintCount, first.Status, first.CheckNumber).ShouldBe((1, "outstanding", 1043));
        (second.PrintCount, second.CheckNumber).ShouldBe((2, 1043));
        second.LastPrintedAt.ShouldNotBeNull();
        second.LastPrintedAt.Value.ShouldBeGreaterThan(first.LastPrintedAt!.Value);
        var read = await ReadAsync(h, issued.Id, ct);
        (read.PrintCount, read.LastPrintedAt).ShouldBe((2, second.LastPrintedAt));
    }
}
