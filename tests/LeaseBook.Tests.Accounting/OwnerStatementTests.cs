using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LeaseBook.Tests.Accounting;

public sealed class StatementSectionMapTests
{
    [Theory]
    [InlineData("RentCharged", StatementSectionKey.Income)]
    [InlineData("FeeCharged", StatementSectionKey.Income)]
    [InlineData("PaymentReceived", StatementSectionKey.Income)]
    [InlineData("ManagementFeeAssessed", StatementSectionKey.OperatingExpenses)]
    [InlineData("VendorPaid", StatementSectionKey.OperatingExpenses)]
    [InlineData("DepositApplied", StatementSectionKey.AppliedDepositsCredits)]
    [InlineData("PrepaymentApplied", StatementSectionKey.AppliedDepositsCredits)]
    [InlineData("CreditIssued", StatementSectionKey.AppliedDepositsCredits)]
    [InlineData("OwnerContribution", StatementSectionKey.Contributions)]
    [InlineData("OwnerDisbursed", StatementSectionKey.Disbursement)]
    public void Maps_each_owner_equity_event_to_its_section(string eventType, StatementSectionKey expected) =>
        StatementSectionMap.Section(eventType).ShouldBe(expected);

    [Fact]
    public void Throws_on_an_unmapped_event_so_the_tie_out_stays_structural() =>
        Should.Throw<UncategorizedEventException>(() => StatementSectionMap.Section("SomeNewEvent"));
}

[Collection(nameof(DatabaseCollection))]
public sealed class OwnerStatementGoldenTests(PostgresFixture fixture)
{
    // CHOSEN AT EXECUTION: O5 (single-property P5, tenant T4) has clean May-2026 activity:
    // RentCharged 1,295 on 2026-05-01 + PaymentReceived 1,295 on 2026-05-28. O1 is multi-property
    // (P1 and P2). O5's balance forward = 21,345.30; May net = +1,295 rent − 1,295 payment = 0.
    // Wait — PaymentReceived is Income (cash increases equity) and RentCharged is also Income (accrual
    // creates receivable that offsets). On cash basis: rent has no owner-equity impact until paid.
    // Actually RentCharged on cash basis posts accrual-only lines; PaymentReceived on cash basis posts
    // cash lines. So O5 cash Income = +1,295 (payment). RentCharged posts 'accrual' basis lines →
    // filtered out on cash. Net cash income for O5 in May = PaymentReceived 1,295.
    // Beginning = 21,345.30 (balance forward, no prior replayed events for O5).
    // Ending = 21,345.30 + 1,295.00 = 22,640.30 (matches GoldenFileTests O5 operating).
    private static readonly Guid Owner = DemoIds.O5;
    private const int Year = 2026, Month = 5;

    [Fact]
    public async Task Statement_ties_out_and_excludes_pm_income()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var stmt = await QueryAsync(db =>
            new GetOwnerStatementDataHandler(db).Handle(
                new GetOwnerStatementData([Owner], PropertyId: null, Year, Month, "cash"), ct), ct);
        var s = stmt.ByOwner[Owner];

        // Structural — figure-independent — assertions:
        s.TieOut.Balanced.ShouldBeTrue();
        s.TieOut.Variance.ShouldBe(0.00m);
        s.TieOut.PmIncomeExcluded.ShouldBeTrue();
        (s.Beginning + s.Sections.Sum(sec => sec.Subtotal)).ShouldBe(s.Ending);
        s.Sections.ShouldNotContain(sec => sec.Lines.Any(l => l.EventType == "ManagementFeeAssessed"
            && l.Amount > 0)); // mgmt fee is an expense (negative) to the owner, never income

        // LOCK AFTER FIRST GREEN RUN (sacred thereafter, like the GoldenFileTests figures):
        s.Beginning.ShouldBe(21_345.30m);
        s.Sections.Single(x => x.Key == StatementSectionKey.Income).Subtotal.ShouldBe(1_295.00m);
        s.Ending.ShouldBe(22_640.30m);
    }

    private async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        T result = default!;
        await executor.RunAsync(DemoSeeder.DemoOrgId, async () => result = await query(db), ct);
        return result;
    }
}
