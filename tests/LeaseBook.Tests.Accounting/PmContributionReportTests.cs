using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// A PM contribution joins the held PM fees but is never earned income (#720), so the management-fee
/// income report leaves it out, and its void too, the same way it leaves out an opening position. The
/// fees the PM earned in the same month still count. Sent through the real dispatcher.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PmContributionReportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_fee_income_report_leaves_out_a_pm_contribution_and_its_void()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = UuidV7.NewId();
        var property = UuidV7.NewId();
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [owner], properties: [property]);
        var feb6 = new DateOnly(2026, 2, 6);
        var sender = AccountingSender.Create(scope, new Leases(new Dictionary<Guid, TenantPostingDimensions>()));

        Guid contribution = default;
        await scope.RunAsync(async () =>
        {
            await Events(scope).PostAsync(new OwnerContribution(
                owner, property, new Money(1_000m), feb6, scope.TrustBankId, "Owner funds"), ct);
            await Events(scope).PostAsync(new ManagementFeeAssessed(
                owner, property, new Money(80m), feb6, scope.TrustBankId, "Management fee"), ct);
            contribution = await Events(scope).PostAsync(new PMContribution(
                new Money(25m), feb6, scope.TrustBankId, "PM contribution"), ct);
        }, ct);
        // Voided in the next month, so the void cannot simply cancel the contribution inside one month.
        await scope.RunAsync(() => sender.Send(
            new VoidEntry(contribution, "recorded twice", new DateOnly(2026, 3, 3), "void:contribution"), ct), ct);

        MgmtFeeIncomeResponse february = null!, march = null!;
        await scope.RunAsync(async () =>
        {
            february = await sender.Query(new GetManagementFeeIncome(2026, 2), ct);
            march = await sender.Query(new GetManagementFeeIncome(2026, 3), ct);
        }, ct);

        february.Rows.ShouldBe([new MgmtFeeIncomeRow(property, 80m)]);
        march.Rows.ShouldBeEmpty();
    }
}
