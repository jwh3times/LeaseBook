using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #475: applying a prepayment draws on the bank that holds it, never on a bank the caller picks. With one
/// holding bank it is used; with several the caller must name one (<c>prepayment_bank_ambiguous</c>); a
/// named bank must itself hold the amount. Each test runs in its own org so golden figures stay put.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PrepaymentApplicationBankTests(PostgresFixture fixture)
{
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Feb5 = new(2026, 2, 5);

    [Fact]
    public async Task An_unnamed_bank_is_the_one_that_holds_the_prepayment()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectPrepayment(ctx.TenantId, 300m, Feb1, ctx.DepositBankId, null, Key()), c), ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(new AddCharge(ctx.TenantId, 300m, Feb1, "rent", null, Key()), c), ct);

        var applied = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new ApplyPrepayment(ctx.TenantId, 300m, Feb5, null, null, Key()), c), ct);

        // Both the liability release and the owner's cash income land on the bank the cash sits in.
        var lines = await ReadLinesAsync(ctx.OrgId, applied.EntryId, ct);
        lines.ShouldContain(l => l.AccountClass == AccountClass.DepositLiability && l.Debit == 300m
            && l.BankAccountId == ctx.DepositBankId);
        lines.ShouldContain(l => l.AccountClass == AccountClass.OwnerEquity && l.Credit == 300m
            && l.BankAccountId == ctx.DepositBankId);
        lines.ShouldNotContain(l => l.BankAccountId == ctx.TrustBankId);
        await AssertSweepCleanAsync(ctx.OrgId, ct);
    }

    [Fact]
    public async Task Prepaid_credit_in_two_banks_must_be_applied_from_a_named_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await HoldInBothBanksAsync(ctx, ct);

        await Should.ThrowAsync<PrepaymentBankAmbiguousException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new ApplyPrepayment(ctx.TenantId, 100m, Feb5, null, null, Key()), c), ct));
    }

    [Fact]
    public async Task A_named_bank_must_hold_the_amount_even_when_the_tenant_total_covers_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await HoldInBothBanksAsync(ctx, ct); // trust 100, deposit 200: the tenant holds 300

        var refused = await Should.ThrowAsync<InsufficientLiabilityException>(() => DispatchAsync(ctx.OrgId,
            (s, c) => s.Send(new ApplyPrepayment(ctx.TenantId, 150m, Feb5, ctx.TrustBankId, null, Key()), c), ct));
        refused.Held.ShouldBe(100m);
        await AssertSweepCleanAsync(ctx.OrgId, ct);
    }

    [Fact]
    public async Task A_named_bank_that_holds_the_amount_is_applied_from()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await HoldInBothBanksAsync(ctx, ct);

        var applied = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new ApplyPrepayment(ctx.TenantId, 150m, Feb5, ctx.DepositBankId, null, Key()), c), ct);

        var lines = await ReadLinesAsync(ctx.OrgId, applied.EntryId, ct);
        lines.ShouldContain(l => l.AccountClass == AccountClass.DepositLiability && l.Debit == 150m
            && l.BankAccountId == ctx.DepositBankId);
        lines.ShouldContain(l => l.AccountClass == AccountClass.OwnerEquity && l.Credit == 150m
            && l.BankAccountId == ctx.DepositBankId);
        await AssertSweepCleanAsync(ctx.OrgId, ct);
    }

    [Fact]
    public async Task An_empty_bank_id_is_a_validation_error_not_a_derivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await HoldInBothBanksAsync(ctx, ct);

        // A nullable Guid's NotEmpty only rejects null; Guid.Empty must still be refused as malformed.
        await Should.ThrowAsync<ValidationException>(() => DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new ApplyPrepayment(ctx.TenantId, 100m, Feb5, Guid.Empty, null, Key()), c), ct));
    }

    [Fact]
    public async Task A_pre_475_cross_bank_application_is_cleared_by_voiding_and_reapplying_from_the_holding_bank()
    {
        // The remediation ADR-050's addendum gives operators for an I10 violation in an existing journal.
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SetupAsync(ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectPrepayment(ctx.TenantId, 75m, Feb1, ctx.DepositBankId, null, Key()), c), ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(new AddCharge(ctx.TenantId, 75m, Feb1, "rent", null, Key()), c), ct);

        // The shape the old path wrote: released from the operating trust, which never held it.
        Guid historical = default;
        await DispatchScopeAsync(ctx.OrgId, async (_, services) => historical =
            await services.GetRequiredService<IPostingService>().PostAsync(
                new PostEntryRequest(Feb1, "PrepaymentApplied", null, "Historical cross-bank application", Key(),
                [
                    new PostLineRequest(AccountCodes.TenantPrepayments, new Money(75m), null, EntryBasis.Both,
                        TenantId: ctx.TenantId, BankAccountId: ctx.TrustBankId),
                    new PostLineRequest(AccountCodes.TenantReceivable, null, new Money(75m), EntryBasis.Accrual,
                        PropertyId: ctx.PropertyId, OwnerId: ctx.OwnerId, TenantId: ctx.TenantId),
                    new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(75m), EntryBasis.Cash,
                        PropertyId: ctx.PropertyId, OwnerId: ctx.OwnerId, BankAccountId: ctx.TrustBankId),
                ]), ct), ct);
        (await SweepAsync(ctx.OrgId, ct)).ShouldHaveSingleItem().Invariant.ShouldBe("I10");

        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new VoidEntry(historical, "Applied from the wrong bank (#475)", Feb5, Key()), c), ct);
        var reapplied = await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new ApplyPrepayment(ctx.TenantId, 75m, Feb5, null, null, Key()), c), ct);

        (await ReadLinesAsync(ctx.OrgId, reapplied.EntryId, ct))
            .ShouldContain(l => l.AccountClass == AccountClass.OwnerEquity && l.BankAccountId == ctx.DepositBankId);
        await AssertSweepCleanAsync(ctx.OrgId, ct);
    }

    /// <summary>Prepaid credit of 100 in the operating trust and 200 in the deposit trust, against 300 owed.</summary>
    private async Task HoldInBothBanksAsync(Ctx ctx, CancellationToken ct)
    {
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectPrepayment(ctx.TenantId, 100m, Feb1, ctx.TrustBankId, null, Key()), c), ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(
            new CollectPrepayment(ctx.TenantId, 200m, Feb1, ctx.DepositBankId, null, Key()), c), ct);
        await DispatchAsync(ctx.OrgId, (s, c) => s.Send(new AddCharge(ctx.TenantId, 300m, Feb1, "rent", null, Key()), c), ct);
    }

    private sealed record Ctx(Guid OrgId, Guid OwnerId, Guid PropertyId, Guid TenantId, Guid TrustBankId, Guid DepositBankId);

    private sealed record LineView(AccountClass AccountClass, decimal? Debit, decimal? Credit, Guid? BankAccountId);

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<IReadOnlyList<LineView>> ReadLinesAsync(Guid orgId, Guid entryId, CancellationToken ct)
    {
        IReadOnlyList<LineView> lines = [];
        await DispatchScopeAsync(orgId, async (_, services) =>
        {
            lines = await services.GetRequiredService<AppDbContext>().Set<JournalLine>().AsNoTracking()
                .Where(l => l.EntryId == entryId)
                .Select(l => new LineView(l.AccountClass,
                    l.Debit == null ? null : l.Debit.Value.Amount,
                    l.Credit == null ? null : l.Credit.Value.Amount,
                    l.BankAccountId))
                .ToListAsync(ct);
        }, ct);
        return lines;
    }

    private async Task AssertSweepCleanAsync(Guid orgId, CancellationToken ct) =>
        (await SweepAsync(orgId, ct)).ShouldBeEmpty();

    private async Task<IReadOnlyList<InvariantViolation>> SweepAsync(Guid orgId, CancellationToken ct)
    {
        IReadOnlyList<InvariantViolation> violations = [];
        await DispatchScopeAsync(orgId, async (_, services) =>
            violations = await services.GetRequiredService<IInvariantChecks>().CheckCoreAsync(ct), ct);
        return violations;
    }

    private async Task<Ctx> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Prepayment Bank Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        Ctx ctx = null!;
        await DispatchScopeAsync(orgId, async (s, _) =>
        {
            var ownerId = await s.Send(new CreateOwner("Owner", null, null, null, 800, 0m), ct);
            var propertyId = await s.Send(new CreateProperty(ownerId, "412 Oakmont Ave", "Asheville", "NC", "28801", null), ct);
            var unitId = await s.Send(new CreateUnit(propertyId, "#2B", 1450m, "available"), ct);
            var tenantId = await s.Send(new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await s.Send(new CreateLease(tenantId, unitId, new DateOnly(2025, 6, 1), new DateOnly(2026, 5, 31), 1450m, 1450m, "active"), ct);
            var trust = await s.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            var deposit = await s.Send(new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct);
            ctx = new Ctx(orgId, ownerId, propertyId, tenantId, trust.Id, deposit.Id);
        }, ct);
        return ctx;
    }

    private async Task DispatchScopeAsync(Guid orgId, Func<ISender, IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        await executor.RunAsSystemAsync(orgId, "test-harness",
            () => work(scope.ServiceProvider.GetRequiredService<ISender>(), scope.ServiceProvider), ct);
    }

    private async Task<T> DispatchAsync<T>(Guid orgId, Func<ISender, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        T result = default!;
        await DispatchScopeAsync(orgId, async (s, _) => result = await work(s, ct), ct);
        return result;
    }
}
