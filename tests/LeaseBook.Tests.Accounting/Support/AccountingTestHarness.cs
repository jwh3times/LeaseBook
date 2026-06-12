using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.Modules.Accounting.Provisioning;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Tests.Accounting.Support;

/// <summary>
/// Shared setup for engine tests: a fresh org with a provisioned chart of accounts (one trust, one
/// deposit, one operating bank) and factories for the module-internal services bound to that scope's
/// context. Construction goes through the real services — never raw SQL — so the engine is always the
/// thing under test (§A money-path discipline).
/// </summary>
internal static class AccountingTestHarness
{
    public static readonly Guid TrustBankId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    public static readonly Guid DepositBankId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    public static readonly Guid OperatingBankId = Guid.Parse("00000000-0000-0000-0000-0000000000b3");

    public static async Task<OrgScope> ProvisionedScopeAsync(PostgresFixture fixture, CancellationToken ct)
    {
        var scope = await OrgScope.CreateAsync(fixture, ct);
        await scope.RunAsync(() => new ChartOfAccounts(scope.Db).ProvisionAsync(
            [
                new BankAccountSpec(TrustBankId, "Operating Trust", BankPurpose.Trust),
                new BankAccountSpec(DepositBankId, "Deposit Trust", BankPurpose.Deposit),
                new BankAccountSpec(OperatingBankId, "Management Operating", BankPurpose.Operating),
            ], ct), ct);
        return scope;
    }

    public static PostingService Posting(OrgScope scope) => new(scope.Db, scope.Tenant, new AccountingPeriods(scope.Db));

    public static ReversalService Reversal(OrgScope scope) => new(scope.Db, scope.Tenant, Posting(scope));

    public static AccountingPeriods Periods(OrgScope scope) => new(scope.Db);

    public static AccountingEventService Events(OrgScope scope) =>
        new(scope.Db, Posting(scope), new PostingLock(scope.Db, scope.Tenant));

    /// <summary>A posted line projected to its resolved account code + amounts/basis/dims, for exact assertions.</summary>
    public sealed record LineView(
        string Code, decimal? Debit, decimal? Credit, EntryBasis Basis,
        Guid? TenantId, Guid? OwnerId, Guid? PropertyId, Guid? UnitId, Guid? BankAccountId);

    public static async Task<List<LineView>> ReadLinesAsync(OrgScope scope, Guid entryId, CancellationToken ct)
    {
        List<LineView> lines = [];
        await scope.RunAsync(async () =>
        {
            var rows = await (
                from line in scope.Db.Set<JournalLine>().AsNoTracking()
                join account in scope.Db.Set<Account>().AsNoTracking() on line.AccountId equals account.Id
                where line.EntryId == entryId
                select new
                {
                    account.Code,
                    line.Debit,
                    line.Credit,
                    line.Basis,
                    line.TenantId,
                    line.OwnerId,
                    line.PropertyId,
                    line.UnitId,
                    line.BankAccountId,
                }).ToListAsync(ct);

            lines = rows
                .Select(r => new LineView(
                    r.Code, r.Debit?.Amount, r.Credit?.Amount, r.Basis,
                    r.TenantId, r.OwnerId, r.PropertyId, r.UnitId, r.BankAccountId))
                .ToList();
        }, ct);

        return lines;
    }
}
