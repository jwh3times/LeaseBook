using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Provisioning;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-03: the per-org chart of accounts is provisioned from the §C.2 code template, idempotently, and
/// stays isolated per org by RLS.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ChartOfAccountsTests(PostgresFixture fixture)
{
    private static readonly BankAccountSpec OperatingTrust = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "Operating Trust", BankPurpose.Trust);
    private static readonly BankAccountSpec DepositTrust = new(Guid.Parse("00000000-0000-0000-0000-0000000000a2"), "Deposit Trust", BankPurpose.Deposit);
    private static readonly BankAccountSpec ManagementOperating = new(Guid.Parse("00000000-0000-0000-0000-0000000000a3"), "Management Operating", BankPurpose.Operating);

    [Fact]
    public async Task Provisioning_creates_exactly_the_template_account_set()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await OrgScope.CreateAsync(fixture, ct);

        await ProvisionAsync(scope, [OperatingTrust, DepositTrust, ManagementOperating], ct);
        var accounts = await ReadAccountsAsync(scope, ct);

        accounts.Select(a => a.Code).ShouldBe(
            [
                AccountCodes.TenantReceivable,
                AccountCodes.OwnerEquity,
                AccountCodes.SecurityDepositsHeld,
                AccountCodes.TenantPrepayments,
                AccountCodes.PmIncome,
                AccountCodes.TrustBank(OperatingTrust.BankAccountId),
                AccountCodes.TrustBank(DepositTrust.BankAccountId),
                AccountCodes.PmOperatingBank(ManagementOperating.BankAccountId),
            ],
            ignoreOrder: true);

        // The two deposit-liability accounts are distinct accounts of the same class (P35).
        accounts.Single(a => a.Code == AccountCodes.SecurityDepositsHeld).Class.ShouldBe(AccountClass.DepositLiability);
        accounts.Single(a => a.Code == AccountCodes.TenantPrepayments).Class.ShouldBe(AccountClass.DepositLiability);

        // Bank accounts carry their class + bank id; singletons carry neither bank id.
        var trust = accounts.Single(a => a.Code == AccountCodes.TrustBank(OperatingTrust.BankAccountId));
        trust.Class.ShouldBe(AccountClass.TrustBank);
        trust.BankAccountId.ShouldBe(OperatingTrust.BankAccountId);
        accounts.Single(a => a.Code == AccountCodes.PmOperatingBank(ManagementOperating.BankAccountId)).Class
            .ShouldBe(AccountClass.PmOperatingBank);
        accounts.Single(a => a.Code == AccountCodes.PmIncome).BankAccountId.ShouldBeNull();
    }

    [Fact]
    public async Task Provisioning_twice_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await OrgScope.CreateAsync(fixture, ct);

        await ProvisionAsync(scope, [OperatingTrust, ManagementOperating], ct);
        await ProvisionAsync(scope, [OperatingTrust, ManagementOperating], ct);

        var accounts = await ReadAccountsAsync(scope, ct);
        accounts.Count.ShouldBe(7); // 5 singletons + 2 banks, no duplicates
    }

    [Fact]
    public async Task Two_orgs_provision_independently()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var orgA = await OrgScope.CreateAsync(fixture, ct);
        await using var orgB = await OrgScope.CreateAsync(fixture, ct);

        await ProvisionAsync(orgA, [OperatingTrust], ct);                 // A gets a trust bank
        await ProvisionAsync(orgB, [ManagementOperating], ct);           // B gets an operating bank

        var aCodes = (await ReadAccountsAsync(orgA, ct)).Select(a => a.Code).ToHashSet(StringComparer.Ordinal);
        var bCodes = (await ReadAccountsAsync(orgB, ct)).Select(a => a.Code).ToHashSet(StringComparer.Ordinal);

        aCodes.ShouldContain(AccountCodes.TrustBank(OperatingTrust.BankAccountId));
        aCodes.ShouldNotContain(AccountCodes.PmOperatingBank(ManagementOperating.BankAccountId)); // B's bank invisible to A
        bCodes.ShouldContain(AccountCodes.PmOperatingBank(ManagementOperating.BankAccountId));
        bCodes.ShouldNotContain(AccountCodes.TrustBank(OperatingTrust.BankAccountId)); // A's bank invisible to B
    }

    private static Task ProvisionAsync(OrgScope scope, IReadOnlyList<BankAccountSpec> banks, CancellationToken ct) =>
        scope.RunAsync(() => new ChartOfAccounts(scope.Db).ProvisionAsync(banks, ct), ct);

    private static async Task<List<Account>> ReadAccountsAsync(OrgScope scope, CancellationToken ct)
    {
        List<Account> accounts = [];
        await scope.RunAsync(async () =>
            accounts = await scope.Db.Set<Account>().AsNoTracking().ToListAsync(ct), ct);
        return accounts;
    }
}
