using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// The refund-check list and the issue form's options (#473, #476). The list is newest first, filtered
/// by bank, tenant or id, a page at a time with the total across every page; its outstanding filter keeps
/// only checks the bank has not paid and that were not voided, which the ledger decides. The options show
/// each held bucket with the next free number on its bank.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RefundCheckListTests(PostgresFixture fixture)
{
    // Five checks across two tenants and both banks:
    //   e  tenant 1, deposit bank, #1000, Mar 3
    //   c  tenant 1, deposit bank, #1002, Mar 2  (cleared by the bank)
    //   a  tenant 0, deposit bank, #1001, Mar 2
    //   b  tenant 0, trust bank,   #1001, Mar 2  (one date and one number with a: the id decides)
    //   d  tenant 0, deposit bank, #1003, Mar 1  (voided)
    private sealed record Checks(RefundCheckView A, RefundCheckView B, RefundCheckView C, RefundCheckView D, RefundCheckView E)
    {
        /// <summary>a and b in the order the database sorts their ids, the later id first.</summary>
        public RefundCheckView[] Tied => string.CompareOrdinal(A.Id.ToString(), B.Id.ToString()) > 0 ? [A, B] : [B, A];

        public RefundCheckView[] Newest => [E, C, .. Tied, D];
    }

    private static async Task<Checks> IssueFiveAsync(RefundCheckHarness h, CancellationToken ct)
    {
        await h.HoldDepositAsync(1450m, ct);
        await h.HoldPrepaymentAsync(75m, ct);
        await h.HoldDepositAsync(1450m, ct, tenant: 1);
        var mar2 = RefundCheckHarness.Issued;
        var a = await h.SendAsync(h.Issue(1001, date: mar2), ct);
        var b = await h.SendAsync(h.Issue(1001, 75m, "prepayment", date: mar2), ct);
        var c = await h.SendAsync(h.Issue(1002, tenant: 1, date: mar2), ct);
        var d = await h.SendAsync(h.Issue(1003, date: mar2.AddDays(-1)), ct);
        var e = await h.SendAsync(h.Issue(1000, tenant: 1, date: mar2.AddDays(1)), ct);
        await h.ClearAsync(c.EntryId, ct);
        await h.SendAsync(new VoidIssuedRefundCheck(d.Id, "Misprinted"), ct);
        return new(a, b, c, d, e);
    }

    private static Guid[] Ids(RefundCheckPage page) => [.. page.Items.Select(x => x.Id)];

    private static Guid[] Ids(IEnumerable<RefundCheckView> checks) => [.. checks.Select(x => x.Id)];

    [Fact]
    public async Task Every_check_is_listed_newest_first_with_the_status_the_ledger_gives_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        var checks = await IssueFiveAsync(h, ct);

        var all = await h.QueryAsync(new GetRefundChecks(), ct);

        Ids(all).ShouldBe(Ids(checks.Newest));
        (all.Total, all.Page, all.PageSize).ShouldBe((5, 1, 50));
        all.Items.Select(x => x.Status).ShouldBe(["outstanding", "cleared", "outstanding", "outstanding", "voided"]);
        all.Items[^1].VoidEntryId.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_list_filters_by_bank_tenant_and_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        var checks = await IssueFiveAsync(h, ct);

        Ids(await h.QueryAsync(new GetRefundChecks(BankAccountId: h.TrustBankId), ct)).ShouldBe([checks.B.Id]);
        Ids(await h.QueryAsync(new GetRefundChecks(TenantId: h.Tenants[1]), ct)).ShouldBe([checks.E.Id, checks.C.Id]);
        Ids(await h.QueryAsync(new GetRefundChecks(Id: checks.A.Id), ct)).ShouldBe([checks.A.Id]);
        var tenantOnBank = await h.QueryAsync(new GetRefundChecks(BankAccountId: h.DepositBankId, TenantId: h.Tenants[0]), ct);
        Ids(tenantOnBank).ShouldBe([checks.A.Id, checks.D.Id]);
        tenantOnBank.Total.ShouldBe(2);
    }

    [Fact]
    public async Task A_page_holds_its_share_of_the_list_and_the_total_counts_every_page()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        var checks = await IssueFiveAsync(h, ct);

        var second = await h.QueryAsync(new GetRefundChecks(Page: 2, PageSize: 2), ct);
        var last = await h.QueryAsync(new GetRefundChecks(Page: 3, PageSize: 2), ct);

        Ids(second).ShouldBe(Ids(checks.Tied));
        (second.Total, second.Page, second.PageSize).ShouldBe((5, 2, 2));
        Ids(last).ShouldBe([checks.D.Id]);
    }

    [Fact]
    public async Task The_outstanding_filter_leaves_out_paid_and_voided_checks_and_pages_what_is_left()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        var checks = await IssueFiveAsync(h, ct);
        RefundCheckView[] outstanding = [checks.E, .. checks.Tied];

        var whole = await h.QueryAsync(new GetRefundChecks(Status: "outstanding"), ct);
        var first = await h.QueryAsync(new GetRefundChecks(Status: "outstanding", Page: 1, PageSize: 2), ct);
        var second = await h.QueryAsync(new GetRefundChecks(Status: "outstanding", Page: 2, PageSize: 2), ct);
        var onTrust = await h.QueryAsync(new GetRefundChecks(BankAccountId: h.TrustBankId, Status: "outstanding"), ct);

        Ids(whole).ShouldBe(Ids(outstanding));
        whole.Items.ShouldAllBe(x => x.Status == "outstanding");
        Ids(first).ShouldBe(Ids(outstanding[..2]));
        Ids(second).ShouldBe(Ids(outstanding[2..]));
        (whole.Total, first.Total, second.Total, second.Page).ShouldBe((3, 3, 3, 2));
        Ids(onTrust).ShouldBe([checks.B.Id]);
    }

    [Fact]
    public async Task A_list_asks_the_ledger_for_statuses_once_and_an_empty_page_not_at_all()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await IssueFiveAsync(h, ct);

        // The outstanding filter reads every candidate's status in one batch, and shows each check with the
        // status it was filtered on rather than asking again.
        async Task<int> ReadsFor(GetRefundChecks query)
        {
            var before = h.StatusReads;
            await h.QueryAsync(query, ct);
            return h.StatusReads - before;
        }

        (await ReadsFor(new GetRefundChecks(Status: "outstanding"))).ShouldBe(1);
        (await ReadsFor(new GetRefundChecks())).ShouldBe(1);
        (await ReadsFor(new GetRefundChecks(Page: 10))).ShouldBe(0);
    }

    [Fact]
    public async Task The_options_show_each_held_bucket_with_the_next_number_on_its_bank()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        await h.HoldPrepaymentAsync(75m, ct);

        // Before the first check on a bank there is no next number: the user types the stock's first one.
        var before = await h.QueryAsync(new GetRefundCheckOptions(h.Tenants[0]), ct);
        before.Funds.Select(f => (f.Source, f.BankAccountId, f.Held, f.NextCheckNumber)).ShouldBe(
            [("deposit", h.DepositBankId, 1450m, (int?)null), ("prepayment", h.TrustBankId, 75m, null)], ignoreOrder: true);

        await h.SendAsync(h.Issue(1005), ct);
        await h.SendAsync(h.Issue(1001), ct);

        var after = await h.QueryAsync(new GetRefundCheckOptions(h.Tenants[0]), ct);
        after.Funds.Select(f => (f.Source, f.BankAccountId, f.Held, f.NextCheckNumber)).ShouldBe(
            [("deposit", h.DepositBankId, 1250m, (int?)1006), ("prepayment", h.TrustBankId, 75m, null)], ignoreOrder: true);
        after.Funds.Single(f => f.Source == "deposit").PropertyId.ShouldBe(h.PropertyId);
    }
}
