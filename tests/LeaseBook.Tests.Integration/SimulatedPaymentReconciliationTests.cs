using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Banking.Domain;
using LeaseBook.Modules.Banking.Features.Import;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// One payout reconciles as one statement line (ADR-053, #500): the import matches the statement line
/// to the whole group of bank lines the payout posted, and the group clears together or not at all.
/// Driven through the real host: the payout arrives by its signed callback, the statement by upload.
/// </summary>
public sealed partial class SimulatedPaymentTests
{
    private static readonly object StatementColumns = new { date = "Date", description = "Description", amount = "Amount" };

    [Fact]
    public async Task A_payout_matches_one_statement_line_and_clears_every_one_of_its_bank_lines_together()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var two = await h.Login(2, ct);
        using var admin = await h.LoginAdmin(ct);
        (await admin.PutAsJsonAsync("/api/settings/payment-fees", FeeRules, ct)).EnsureSuccessStatusCode();
        await HoldFees(h, 80m, ct);
        var clean = await Pay(h, two, 1000m, ct);
        var shortfall = await Pay(h, one, 500m, ct);
        var surplus = await Pay(h, one, 250m, ct);
        await h.Settle(h.Payout("po_1", 1742.90m,
            h.Line("1", "Payment", clean, 1030.17m, 30.17m, 1000m),
            h.Line("2", "Payment", shortfall, 515.24m, 22.74m, 492.50m),
            h.Line("3", "Payment", surplus, 257.78m, 7.38m, 250.40m)), ct);

        var register = await RegisterRows(admin, h, ct);
        var payoutLines = register.Where(r => r.PayoutReference == "po_1").ToList();
        payoutLines.Count.ShouldBe(5);
        var lone = register.Single(r => r.PayoutReference is null);
        var day = payoutLines[0].Date.ToString("yyyy-MM-dd");

        // The bank shows the payout as one deposit. The second line is the amount of ONE of the payout's
        // bank lines; the third is the fees held before it, a line no payout posted.
        var csv = $"Date,Description,Amount\n{day},PROCESSOR PAYOUT,1742.90\n{day},CARD 1000,1000.00\n{lone.Date:yyyy-MM-dd},INTEREST,80.00\n";
        var import = await PostJson<ImportResult>(admin, $"/api/banking/banks/{h.Binding.BankId}/imports",
            new { filename = "statement.csv", csvContent = csv, columnMap = StatementColumns }, ct);
        import.Imported.ShouldBe(3);

        var preview = (await admin.GetFromJsonAsync<MatchPreviewResponse>($"/api/banking/imports/{import.ImportId}/matches", ct))!;
        var payoutRow = preview.Rows.Single(r => r.Description == "PROCESSOR PAYOUT");
        (payoutRow.Kind, payoutRow.GroupRef, payoutRow.JournalLineId, payoutRow.CandidateAmount, payoutRow.CandidateDescription)
            .ShouldBe(("matched", "po_1", null, 1742.90m, "Payout po_1"));
        payoutRow.GroupLines!.Select(l => l.JournalLineId).ShouldBe(payoutLines.Select(l => l.JournalLineId), ignoreOrder: true);
        payoutRow.GroupLines!.Sum(l => l.Amount).ShouldBe(1742.90m);
        // A bank line that belongs to the payout is never offered by itself.
        var partRow = preview.Rows.Single(r => r.Description == "CARD 1000");
        (partRow.Kind, partRow.JournalLineId, partRow.GroupRef).ShouldBe(("unmatched", null, null));
        var loneRow = preview.Rows.Single(r => r.Description == "INTEREST");
        (loneRow.Kind, loneRow.JournalLineId, loneRow.GroupRef).ShouldBe(("matched", lone.JournalLineId, null));

        var confirmUrl = $"/api/banking/imports/{import.ImportId}/confirm";
        var thousand = payoutLines.Single(l => l.Deposit == 1000m).JournalLineId;
        object Decide(MatchPreviewRow row, Guid? journalLineId, string? groupRef, string kind = "matched") =>
            new { statementLineId = row.StatementLineId, journalLineId, kind, groupRef };

        // A request the preview never offered is refused whole: one of the payout's lines by itself, the
        // payout against a statement line of another amount, a payout nobody posted, and both at once.
        foreach (var refused in new[]
        {
            Decide(partRow, thousand, null),
            Decide(partRow, null, "po_1"),
            Decide(payoutRow, null, "po_9"),
            Decide(payoutRow, thousand, "po_1"),
            Decide(payoutRow, null, "po_1", "created"),
        })
        {
            (await admin.PostAsJsonAsync(confirmUrl, new { decisions = new[] { Decide(loneRow, lone.JournalLineId, null), refused } }, ct))
                .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        (await RegisterRows(admin, h, ct)).ShouldAllBe(r => r.Status == BankLineStatus.Uncleared);
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<StatementMatch>().CountAsync(ct)).ShouldBe(0), ct);

        var confirmed = await PostJson<ConfirmMatchesResult>(admin, confirmUrl, new
        {
            decisions = new[]
            {
                Decide(payoutRow, null, "po_1"), Decide(partRow, null, null, "unmatched"), Decide(loneRow, lone.JournalLineId, null),
            },
        }, ct);
        (confirmed.Cleared, confirmed.Recorded).ShouldBe((6, 3));
        confirmed.UnmatchedLineIds.ShouldBe([partRow.StatementLineId]);
        (await RegisterRows(admin, h, ct)).ShouldAllBe(r => r.Status == BankLineStatus.Cleared);
        await h.InOrg(async sp =>
        {
            // The audit trail names every bank line the one statement line answered for.
            var matches = await sp.GetRequiredService<AppDbContext>().Set<StatementMatch>().AsNoTracking().ToListAsync(ct);
            matches.Where(m => m.StatementLineId == payoutRow.StatementLineId).Select(m => m.JournalLineId!.Value)
                .ShouldBe(payoutLines.Select(l => l.JournalLineId), ignoreOrder: true);
            matches.Count.ShouldBe(7);
        }, ct);

        // The register's own tick is bound by the same rule: unticking one line of the payout unticks all five.
        (await admin.PostAsJsonAsync("/api/accounting/banks/clearances", new { journalLineIds = new[] { thousand }, cleared = false }, ct))
            .EnsureSuccessStatusCode();
        var after = await RegisterRows(admin, h, ct);
        after.Where(r => r.PayoutReference == "po_1").ShouldAllBe(r => r.Status == BankLineStatus.Uncleared);
        after.Single(r => r.PayoutReference is null).Status.ShouldBe(BankLineStatus.Cleared);
    }

    [Fact]
    public async Task A_payout_whose_lines_moved_after_the_preview_is_refused_at_confirmation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        await h.Settle(h.Payout("po_1", 600m, h.Line("1", "Payment", op, 600m, 0m, 600m)), ct);
        var line = (await RegisterRows(admin, h, ct)).Single(r => r.PayoutReference == "po_1");

        var csv = $"Date,Description,Amount\n{line.Date:yyyy-MM-dd},PROCESSOR PAYOUT,600.00\n";
        var import = await PostJson<ImportResult>(admin, $"/api/banking/banks/{h.Binding.BankId}/imports",
            new { filename = "statement.csv", csvContent = csv, columnMap = StatementColumns }, ct);
        var row = (await admin.GetFromJsonAsync<MatchPreviewResponse>($"/api/banking/imports/{import.ImportId}/matches", ct))!.Rows.Single();
        row.GroupRef.ShouldBe("po_1");

        // A statement far from the payout never reads it as a candidate. Naming its line there is still
        // refused: the line is the payout's whatever the window, and clearing it would clear the payout.
        var far = line.Date.AddDays(120);
        var later = await PostJson<ImportResult>(admin, $"/api/banking/banks/{h.Binding.BankId}/imports",
            new { filename = "later.csv", csvContent = $"Date,Description,Amount\n{far:yyyy-MM-dd},DEPOSIT,600.00\n", columnMap = StatementColumns }, ct);
        var farRow = (await admin.GetFromJsonAsync<MatchPreviewResponse>($"/api/banking/imports/{later.ImportId}/matches", ct))!.Rows.Single();
        (farRow.Kind, farRow.GroupRef).ShouldBe(("unmatched", null));
        var tampered = await admin.PostAsJsonAsync($"/api/banking/imports/{later.ImportId}/confirm",
            new { decisions = new[] { new { statementLineId = farRow.StatementLineId, journalLineId = (Guid?)line.JournalLineId, kind = "matched" } } }, ct);
        tampered.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tampered.Content.ReadAsStringAsync(ct)).ShouldContain("clears only with the whole payout");
        (await RegisterRows(admin, h, ct)).Single(r => r.PayoutReference == "po_1").Status.ShouldBe(BankLineStatus.Uncleared);

        // Somebody ticks the payout in the register between the preview and the confirmation.
        (await admin.PostAsJsonAsync("/api/accounting/banks/clearances", new { journalLineIds = new[] { line.JournalLineId }, cleared = true }, ct))
            .EnsureSuccessStatusCode();

        var response = await admin.PostAsJsonAsync($"/api/banking/imports/{import.ImportId}/confirm",
            new { decisions = new[] { new { statementLineId = row.StatementLineId, journalLineId = (Guid?)null, kind = "matched", groupRef = "po_1" } } }, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("Preview the matches again");
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<StatementMatch>().CountAsync(ct)).ShouldBe(0), ct);
    }

    private static async Task<IReadOnlyList<RegisterRow>> RegisterRows(HttpClient staff, Harness h, CancellationToken ct) =>
        (await staff.GetFromJsonAsync<RegisterResponse>($"/api/accounting/banks/{h.Binding.BankId}/register?pageSize=200", ct))!.Rows;

    private static async Task<T> PostJson<T>(HttpClient client, string url, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(url, body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
