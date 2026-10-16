using FluentValidation;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// A bank account's check stock settings: the print offsets for pre-printed stock (#473), and for blank
/// stock the MICR numbers and the MICR line's own offsets (#474). Each is kept per bank. The numbers are
/// write-only: a read shows their last four digits, a save without one keeps the saved one, and only the
/// print query hands the full numbers to the renderer.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CheckStockSettingsTests(PostgresFixture fixture)
{
    private const string Routing = "111000012";
    private const string OnUs = "12-3456 789U";

    private static Task<BankMicrDetailsView> MicrAsync(RefundCheckHarness h, Guid bank, CancellationToken ct) =>
        h.QueryAsync(new GetBankMicrDetails(bank), ct);

    private static Task<BankMicrDetailsView> SaveMicrAsync(RefundCheckHarness h, Guid bank, string stock, string? routing,
        string? onUs, CancellationToken ct, decimal x = 0m, decimal y = 0m) =>
        h.SendAsync(new SaveBankMicrDetails(bank, stock, routing, onUs, x, y), ct);

    [Fact]
    public async Task Print_offsets_are_zero_until_saved_and_kept_per_bank()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);

        (await h.QueryAsync(new GetCheckPrintSettings(h.TrustBankId), ct)).ShouldBe(new CheckPrintSettingsView(h.TrustBankId, 0m, 0m));
        (await h.SendAsync(new SaveCheckPrintSettings(h.TrustBankId, 3.5m, -2m), ct))
            .ShouldBe(new CheckPrintSettingsView(h.TrustBankId, 3.5m, -2m));
        await h.SendAsync(new SaveCheckPrintSettings(h.DepositBankId, -1m, 1m), ct);
        await h.SendAsync(new SaveCheckPrintSettings(h.TrustBankId, 0.25m, 0.75m), ct);

        (await h.QueryAsync(new GetCheckPrintSettings(h.TrustBankId), ct)).ShouldBe(new CheckPrintSettingsView(h.TrustBankId, 0.25m, 0.75m));
        (await h.QueryAsync(new GetCheckPrintSettings(h.DepositBankId), ct)).ShouldBe(new CheckPrintSettingsView(h.DepositBankId, -1m, 1m));
    }

    [Fact]
    public async Task Micr_details_read_back_as_the_last_four_digits_and_print_in_full_only_for_blank_stock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        var nothingSaved = new BankMicrDetailsView(h.DepositBankId, "preprinted", null, null, 0m, 0m);
        (await MicrAsync(h, h.DepositBankId, ct)).ShouldBe(nothingSaved);
        (await h.QueryAsync(new GetBankMicrPrint(h.DepositBankId), ct)).ShouldBeNull();

        var saved = await SaveMicrAsync(h, h.DepositBankId, "blank", Routing, OnUs, ct, 1.5m, -0.5m);

        // The last four digits of each, skipping the dash, the space and the On-Us symbol.
        var expected = new BankMicrDetailsView(h.DepositBankId, "blank", "0012", "6789", 1.5m, -0.5m);
        saved.ShouldBe(expected);
        (await MicrAsync(h, h.DepositBankId, ct)).ShouldBe(expected);
        (await MicrAsync(h, h.TrustBankId, ct)).ShouldBe(nothingSaved with { BankAccountId = h.TrustBankId });
        var print = (await h.QueryAsync(new GetBankMicrPrint(h.DepositBankId), ct)).ShouldNotBeNull();
        (print.Numbers.RoutingNumber, print.Numbers.OnUsField, print.MicrOffsetXPoints, print.MicrOffsetYPoints)
            .ShouldBe((Routing, OnUs, 1.5m, -0.5m));
        (await h.QueryAsync(new GetBankMicrPrint(h.TrustBankId), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_number_left_out_of_a_save_keeps_the_saved_one_and_a_new_one_replaces_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await SaveMicrAsync(h, h.DepositBankId, "blank", Routing, "123456789U", ct);

        (await SaveMicrAsync(h, h.DepositBankId, "blank", null, null, ct, 2m, 2m))
            .ShouldBe(new BankMicrDetailsView(h.DepositBankId, "blank", "0012", "6789", 2m, 2m));
        (await SaveMicrAsync(h, h.DepositBankId, "blank", "540900071", null, ct)).RoutingNumberLast4.ShouldBe("0071");
        (await SaveMicrAsync(h, h.DepositBankId, "blank", null, "987654321U", ct))
            .ShouldBe(new BankMicrDetailsView(h.DepositBankId, "blank", "0071", "4321", 0m, 0m));
        var print = (await h.QueryAsync(new GetBankMicrPrint(h.DepositBankId), ct)).ShouldNotBeNull();
        (print.Numbers.RoutingNumber, print.Numbers.OnUsField).ShouldBe(("540900071", "987654321U"));

        // Back to pre-printed stock: the numbers are kept, and the stock's own MICR line is printed instead.
        (await SaveMicrAsync(h, h.DepositBankId, "preprinted", null, null, ct))
            .ShouldBe(new BankMicrDetailsView(h.DepositBankId, "preprinted", "0071", "4321", 0m, 0m));
        (await h.QueryAsync(new GetBankMicrPrint(h.DepositBankId), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Blank_stock_needs_both_numbers_saved_or_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);

        (await SaveMicrAsync(h, h.TrustBankId, "preprinted", null, null, ct, 1m, 1m))
            .ShouldBe(new BankMicrDetailsView(h.TrustBankId, "preprinted", null, null, 1m, 1m));
        foreach (var (routing, onUs) in new[] { (Routing, (string?)null), (null, OnUs), (null, null) })
        {
            var refused = await Should.ThrowAsync<ValidationException>(() => SaveMicrAsync(h, h.DepositBankId, "blank", routing, onUs, ct));
            var failure = refused.Errors.ShouldHaveSingleItem();
            (failure.PropertyName, failure.ErrorMessage)
                .ShouldBe(("StockKind", "Blank check stock needs the routing number and the On-Us field."));
        }

        (await MicrAsync(h, h.DepositBankId, ct)).ShouldBe(new BankMicrDetailsView(h.DepositBankId, "preprinted", null, null, 0m, 0m));
        (await SaveMicrAsync(h, h.DepositBankId, "blank", Routing, OnUs, ct)).StockKind.ShouldBe("blank");
    }

    [Theory]
    [InlineData(true, "A blank-stock MICR profile has no routing number.")]
    [InlineData(false, "A blank-stock MICR profile has no On-Us field.")]
    public async Task A_blank_stock_profile_missing_a_number_is_refused_at_print_rather_than_printed_without_it(bool routing,
        string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await SaveMicrAsync(h, h.DepositBankId, "blank", Routing, OnUs, ct);

        // No save leaves blank stock without both numbers, so the row is edited by hand to reach the guard.
        await h.InOrg(async _ =>
        {
            var row = await h.Scope.Db.Set<BankMicrProfile>().SingleAsync(x => x.BankAccountId == h.DepositBankId, ct);
            if (routing) { row.RoutingNumber = null; } else { row.OnUsAccountNumber = null; }
            return await h.Scope.Db.SaveChangesAsync(ct);
        }, ct);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => h.QueryAsync(new GetBankMicrPrint(h.DepositBankId), ct));
        refused.Message.ShouldBe(reason);
    }
}
