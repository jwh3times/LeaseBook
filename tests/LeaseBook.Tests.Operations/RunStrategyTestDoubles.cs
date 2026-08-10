using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Runs;

namespace LeaseBook.Tests.Operations;

internal sealed class StubLeaseScheduleData(IReadOnlyList<LeaseScheduleRow> rows) : ILeaseScheduleData
{
    public Task<IReadOnlyList<LeaseScheduleRow>> GetActiveAsync(
        int year,
        int month,
        CancellationToken ct) => Task.FromResult(rows);
}

internal sealed class StubDelinquencyData(IReadOnlyList<DelinquentLedgerRow> rows) : IDelinquencyData
{
    public Task<IReadOnlyList<DelinquentLedgerRow>> GetAsync(
        int year,
        int month,
        DateOnly asOf,
        CancellationToken ct) => Task.FromResult(rows);
}

internal sealed class StubLateFeePolicyData(IReadOnlyDictionary<Guid, LateFeePolicy> policies)
    : ILateFeePolicyData
{
    public Task<IReadOnlyDictionary<Guid, LateFeePolicy>> GetAsync(
        IReadOnlyList<Guid> leaseIds,
        CancellationToken ct) => Task.FromResult(policies);
}

internal sealed class StubPostedSourceRefs(IReadOnlySet<string>? existing = null) : IPostedSourceRefs
{
    public Task<IReadOnlySet<string>> GetExistingAsync(
        IReadOnlyList<string> candidateKeys,
        CancellationToken ct) =>
        Task.FromResult(existing ?? (IReadOnlySet<string>)new HashSet<string>());
}

internal sealed class StubPeriodChargeGuard(IReadOnlySet<Guid>? chargedTenants = null)
    : IPeriodChargeGuard
{
    public Task<IReadOnlySet<Guid>> GetChargedTenantsAsync(
        string eventType,
        string? eventSubtype,
        int year,
        int month,
        IReadOnlyList<Guid> tenantIds,
        CancellationToken ct) =>
        Task.FromResult(chargedTenants ?? (IReadOnlySet<Guid>)new HashSet<Guid>());
}

internal sealed class StubOwnerDisbursementData(IReadOnlyList<OwnerDisbursementRow> rows)
    : IOwnerDisbursementData
{
    public Task<IReadOnlyList<OwnerDisbursementRow>> GetAsync(CancellationToken ct) =>
        Task.FromResult(rows);
}

internal sealed class StubOwnerEquityBalances(IReadOnlyDictionary<Guid, decimal> balances)
    : IOwnerEquityBalances
{
    public Task<IReadOnlyDictionary<Guid, decimal>> GetAsync(
        IReadOnlyList<Guid> ownerIds,
        string basis,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, decimal>>(
            ownerIds.Where(balances.ContainsKey).ToDictionary(id => id, id => balances[id]));
}

internal sealed class StubBankAccountInfo(Guid? operatingBankId = null) : IBankAccountInfo
{
    private static readonly Guid DefaultBankId = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");

    public Task<(Guid OperatingBankId, string Display)> GetOperatingTrustAsync(CancellationToken ct) =>
        Task.FromResult((operatingBankId ?? DefaultBankId, "Test Trust Bank"));
}
