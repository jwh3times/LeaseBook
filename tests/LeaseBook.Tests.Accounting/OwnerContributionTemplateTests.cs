using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The <c>OwnerContribution</c> template takes an owner's money into an operating trust account only
/// (#744, ADR-014 addendum). Owner money in a security-deposit trust sits in the wrong trust, and the PM
/// operating account is outside trust, so the template refuses both whoever posts it: the register's
/// command, a seeder or a direct call.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OwnerContributionTemplateTests(PostgresFixture fixture)
{
    // Per test: directory ids are global, so each test's org seeds its own.
    private readonly Guid Owner = UuidV7.NewId();
    private readonly Guid Property = UuidV7.NewId();
    private static readonly DateOnly Date = new(2026, 2, 6);

    [Fact]
    public async Task A_contribution_into_the_operating_trust_raises_the_bank_and_the_owners_equity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);

        var id = await PostAsync(scope, Contribution(scope.TrustBankId, "staff only"), ct);

        (await ReadLinesAsync(scope, id, ct)).ShouldBe(
        [
            new(AccountCodes.TrustBank(scope.TrustBankId), 125m, null, EntryBasis.Both,
                null, null, null, null, scope.TrustBankId),
            new(AccountCodes.OwnerEquity, null, 125m, EntryBasis.Both,
                null, Owner, Property, null, scope.TrustBankId),
        ], ignoreOrder: true);
        string? note = null;
        await scope.RunAsync(async () => note = await scope.Db.Set<JournalEntry>()
            .Where(e => e.Id == id).Select(e => e.InternalNote).SingleAsync(ct), ct);
        note.ShouldBe("staff only");
    }

    [Fact]
    public async Task A_contribution_into_the_security_deposit_trust_is_refused_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);

        var error = await Should.ThrowAsync<ContributionBankNotOperatingTrustException>(
            () => PostAsync(scope, Contribution(scope.DepositBankId), ct));

        (error.Code, error.BankAccountId).ShouldBe(("contribution_bank_not_operating_trust", scope.DepositBankId));
        (await EntryCountAsync(scope, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_contribution_into_the_pm_operating_account_is_refused_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);

        var error = await Should.ThrowAsync<ContributionBankNotOperatingTrustException>(
            () => PostAsync(scope, Contribution(scope.OperatingBankId), ct));

        (error.Code, error.BankAccountId).ShouldBe(("contribution_bank_not_operating_trust", scope.OperatingBankId));
        (await EntryCountAsync(scope, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_contribution_into_an_id_that_is_no_bank_account_stays_an_unknown_account()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [Owner], properties: [Property]);
        var stranger = UuidV7.NewId();

        var error = await Should.ThrowAsync<UnknownAccountException>(
            () => PostAsync(scope, Contribution(stranger), ct));

        error.Code.ShouldBe("unknown_account");
        (await EntryCountAsync(scope, ct)).ShouldBe(0);
    }

    private OwnerContribution Contribution(Guid bank, string? internalNote = null) =>
        new(Owner, Property, new Money(125m), Date, bank, "Owner contribution", InternalNote: internalNote);

    private static async Task<Guid> PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task<int> EntryCountAsync(OrgScope scope, CancellationToken ct)
    {
        var count = 0;
        await scope.RunAsync(async () => count = await scope.Db.Set<JournalEntry>().CountAsync(ct), ct);
        return count;
    }
}
