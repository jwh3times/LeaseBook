using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Audit;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Reporting;
using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-8: the trust-compliance pack assembler composes four audit artifacts for a trust account × period
/// from existing reads (no new figures), and its cover ties the period-end trust equation to the ledger
/// movement. Read-only — asserted against the sacred demo (Tarheel) org without mutating it.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CompliancePackAssemblerTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Pack_composes_four_artifacts_and_the_cover_ties_to_the_ledger()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        var from = new DateOnly(2026, 1, 1);
        var to = DateOnly.FromDateTime(DateTime.UtcNow); // includes the seed-time audit rows

        var pack = await AssembleAsync(DemoIds.OperBank, from, to, ct);

        // Exactly the four audit artifacts — and NOT the PM-facing management-fee report.
        CompliancePack.ArtifactNames.Count.ShouldBe(4);
        CompliancePack.ArtifactNames.ShouldNotContain("management-fee-income");

        // Cover ties out: opening (0 pre-cutover) + period movement == period-end book == sacred figure,
        // with variance 0.00.
        pack.Cover.ClosingEquation.Variance.ShouldBe(0m);
        pack.Cover.ClosingEquation.Book.ShouldBe(248_930.14m);
        pack.Cover.OpeningBook.ShouldBe(0m);
        pack.TrustLedger.ShouldNotBeEmpty();
        var movement = pack.TrustLedger.Sum(r => (r.Deposit ?? 0m) - (r.Withdrawal ?? 0m));
        (pack.Cover.OpeningBook + movement).ShouldBe(pack.Cover.ClosingEquation.Book);

        // Deposit register present and non-negative as of the period end.
        pack.DepositRegister.ShouldAllBe(r => r.Held >= 0m);

        // Audit trail carries only money-touching events.
        pack.AuditTrail.ShouldNotBeEmpty();
        pack.AuditTrail.ShouldAllBe(r => AuditExtractReader.MoneyTouchingEntityTypes.Contains(r.EntityType));
    }

    private async Task<CompliancePack> AssembleAsync(Guid bankId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        CompliancePack pack = default!;
        await executor.RunAsync(DemoSeeder.DemoOrgId, async () =>
        {
            var assembler = new CompliancePackAssembler(
                sp.GetRequiredService<ISender>(),
                new AuditExtractReader(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<ITenantContext>()));
            pack = await assembler.AssembleAsync(bankId, from, to, ct);
        }, ct);
        return pack;
    }
}
