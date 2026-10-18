using LeaseBook.Modules.Accounting;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LeaseBook.Tests.Payments.Support;

/// <summary>
/// One organization with a trust bank, a deposit bank, a property and two tenants, and the refund-check
/// commands and queries dispatched over it through the real pipeline (validation, then the handler), with
/// no host.
/// <para>
/// Unlike the engines' stand-in ledger, the refund-check ledger here is the host's own adapter
/// (<c>LeaseBook.Web.Payments.RefundCheckLedgerAdapter</c>) over the real Accounting commands, so the
/// bank a refund draws on, the held-balance guard, the guarded void and the derived statuses are
/// Accounting's answers, not the test's. The adapter is internal to the host, so it is registered by
/// name; a rename fails every test here at once. A thin counter around it records how often Payments
/// asks for statuses.
/// </para>
/// </summary>
internal sealed class RefundCheckHarness : IAsyncDisposable
{
    /// <summary>The date the held funds were collected.</summary>
    public static readonly DateOnly Collected = new(2026, 2, 1);

    /// <summary>The date every check is issued on unless a test says otherwise.</summary>
    public static readonly DateOnly Issued = new(2026, 3, 2);

    private static readonly Type LedgerAdapter = typeof(LeaseBook.Web.Payments.BlankCheckStock).Assembly
        .GetType("LeaseBook.Web.Payments.RefundCheckLedgerAdapter", throwOnError: true)!;

    private readonly PostgresFixture _fixture;
    private readonly ServiceProvider _services;

    private RefundCheckHarness(PostgresFixture fixture, OrgScope scope, ServiceProvider services, ManualClock clock,
        Guid trustBankId, Guid depositBankId, Guid ownerId, Guid propertyId, IReadOnlyList<Guid> tenants)
    {
        _fixture = fixture;
        _services = services;
        Scope = scope;
        Clock = clock;
        TrustBankId = trustBankId;
        DepositBankId = depositBankId;
        OwnerId = ownerId;
        PropertyId = propertyId;
        Tenants = tenants;
    }

    public OrgScope Scope { get; }

    public ManualClock Clock { get; }

    /// <summary>The bank a prepayment is held in.</summary>
    public Guid TrustBankId { get; }

    /// <summary>The bank a security deposit is held in.</summary>
    public Guid DepositBankId { get; }

    public Guid OwnerId { get; }

    public Guid PropertyId { get; }

    public IReadOnlyList<Guid> Tenants { get; }

    /// <summary>The dispatcher over <see cref="Scope"/>'s context. Outside <see cref="InOrg{T}"/> it has no organization.</summary>
    public ISender Sender => _services.GetRequiredService<ISender>();

    /// <summary>How many times Payments has asked the ledger for check statuses through <see cref="Sender"/>.</summary>
    public int StatusReads => _services.GetRequiredService<CountingRefundLedger>().StatusReads;

    public static async Task<RefundCheckHarness> CreateAsync(PostgresFixture fixture, CancellationToken ct)
    {
        var scope = await OrgScope.CreateAsync(fixture, ct);
        var clock = new ManualClock();
        var trustBankId = UuidV7.NewId();
        var depositBankId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();
        var propertyId = UuidV7.NewId();
        Guid[] tenants = [UuidV7.NewId(), UuidV7.NewId()];
        var services = ServicesOver(scope.Db, scope.Tenant, scope.Actor, clock);

        await scope.RunAsync(() => services.GetRequiredService<IChartOfAccounts>().ProvisionAsync(
            [
                new BankAccountSpec(trustBankId, "Operating Trust", BankPurpose.Trust),
                new BankAccountSpec(depositBankId, "Deposit Trust", BankPurpose.Deposit),
            ], ct), ct);
        await SeedDirectoryAsync(fixture, scope.OrgId, trustBankId, depositBankId, ownerId, propertyId, tenants, ct);
        scope.Db.ChangeTracker.Clear();

        return new RefundCheckHarness(fixture, scope, services, clock, trustBankId, depositBankId, ownerId, propertyId, tenants);
    }

    /// <summary>
    /// The Accounting module, every handler and validator of Accounting and Payments behind the real
    /// dispatcher, and the host's refund-check ledger adapter, all over one context.
    /// </summary>
    internal static ServiceProvider ServicesOver(DbContext db, IOrgContext org, IActorContext actor, TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAccountingModule();
        services.AddLeaseBookCqrs(typeof(AccountingModuleServiceCollectionExtensions).Assembly,
            typeof(LeaseBook.Modules.Payments.ModuleMarker).Assembly);
        services.AddSingleton(db);
        services.AddSingleton(org);
        services.AddSingleton(actor);
        services.AddSingleton(clock);
        services.AddSingleton(LedgerAdapter);
        services.AddSingleton(sp => new CountingRefundLedger((IRefundCheckLedger)sp.GetRequiredService(LedgerAdapter)));
        services.AddSingleton<IRefundCheckLedger>(sp => sp.GetRequiredService<CountingRefundLedger>());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one organization transaction with nothing tracked from earlier
    /// work, as each request in the host gets a context of its own.
    /// </summary>
    public async Task<T> InOrg<T>(Func<ISender, Task<T>> work, CancellationToken ct)
    {
        T result = default!;
        Scope.Db.ChangeTracker.Clear();
        try
        {
            await Scope.RunAsync(async () => result = await work(Sender), ct);
        }
        finally
        {
            Scope.Db.ChangeTracker.Clear();
        }
        return result;
    }

    public Task<T> SendAsync<T>(ICommand<T> command, CancellationToken ct) => InOrg(s => s.Send(command, ct), ct);

    public Task<T> QueryAsync<T>(IQuery<T> query, CancellationToken ct) => InOrg(s => s.Query(query, ct), ct);

    /// <summary>Collects a security deposit from a tenant into the deposit bank.</summary>
    public Task<Guid> HoldDepositAsync(decimal amount, CancellationToken ct, int tenant = 0) =>
        InOrg(_ => _services.GetRequiredService<IAccountingEvents>().PostAsync(new DepositCollected(
            Tenants[tenant], PropertyId, OwnerId, new Money(amount), Collected, DepositBankId, "Security deposit",
            $"deposit:{UuidV7.NewId()}"), ct), ct);

    /// <summary>Takes a prepayment from a tenant into a bank, the trust bank unless one is named.</summary>
    public Task<Guid> HoldPrepaymentAsync(decimal amount, CancellationToken ct, int tenant = 0, Guid? bank = null) =>
        InOrg(_ => _services.GetRequiredService<IAccountingEvents>().PostAsync(new PrepaymentReceived(
            Tenants[tenant], PropertyId, OwnerId, new Money(amount), Collected, bank ?? TrustBankId, "Prepayment",
            $"prepayment:{UuidV7.NewId()}"), ct), ct);

    /// <summary>A request to issue a check that passes validation, drawn on a held deposit unless a source is named.</summary>
    public IssueRefundCheck Issue(int checkNumber, decimal amount = 100m, string source = "deposit", int tenant = 0,
        Guid? key = null, DateOnly? date = null) => new(
        key ?? UuidV7.NewId(), Tenants[tenant], source, amount, date ?? Issued, checkNumber,
        "Jasmine Carter", "412 Oakmont Ave", "Apt 2B", "Asheville", "NC", "28801", "Deposit refund", null);

    /// <summary>Marks a check's bank line cleared, as a register tick does when the bank pays it.</summary>
    public Task ClearAsync(Guid entryId, CancellationToken ct) => InOrg(async sender =>
    {
        var line = await Scope.Db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.EntryId == entryId && l.AccountClass == AccountClass.TrustBank)
            .Select(l => l.Id).SingleAsync(ct);
        return await sender.Send(new ApplyClearances([line]), ct);
    }, ct);

    public Lane Lane() => new(_fixture, Scope.OrgId, Clock);

    /// <summary>
    /// Runs <paramref name="first"/> on one connection and holds its transaction open; starts
    /// <paramref name="second"/> on another; waits until the second has finished or is waiting on a
    /// lock; and only then commits the first. See <see cref="PaymentsHarness.RaceAsync{T1, T2}"/>.
    /// </summary>
    public async Task<Race<T1, T2>> RaceAsync<T1, T2>(Func<Lane, Task<T1>> first, Func<Lane, Task<T2>> second, CancellationToken ct)
    {
        await using var a = Lane();
        await using var b = Lane();
        var held = a.Hold(first, ct);
        var firstResult = await held.Done;
        var running = b.RunAsync(second, ct);
        await Lanes.WaitingOrDoneAsync(_fixture, b, running, ct);
        held.Commit();
        await held.Ended();
        var (secondResult, error) = await Lanes.OutcomeOf(running);
        Scope.Db.ChangeTracker.Clear();
        return new(firstResult, secondResult, error, a.Ledger, b.Ledger);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await Scope.DisposeAsync();
    }

    // The directory rows the journal's dimension columns have foreign keys to, written as the migrator
    // under this org's context, as the payments and accounting harnesses write them. Key targets only.
    private static async Task SeedDirectoryAsync(PostgresFixture fixture, Guid orgId, Guid trustBankId, Guid depositBankId,
        Guid ownerId, Guid propertyId, IReadOnlyList<Guid> tenants, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);

        await InsertAsync(conn, tx, ct,
            "INSERT INTO bank_accounts (id, org_id, name, purpose, created_at) VALUES (@id, @org, 'Operating Trust', 'trust', now())",
            trustBankId, orgId);
        await InsertAsync(conn, tx, ct,
            "INSERT INTO bank_accounts (id, org_id, name, purpose, created_at) VALUES (@id, @org, 'Deposit Trust', 'deposit', now())",
            depositBankId, orgId);
        await InsertAsync(conn, tx, ct,
            "INSERT INTO owners (id, org_id, name, created_at) VALUES (@id, @org, 'Refund test owner', now())",
            ownerId, orgId);
        await InsertAsync(conn, tx, ct,
            $"INSERT INTO properties (id, org_id, owner_id, address, created_at) VALUES (@id, @org, '{ownerId}', '412 Oakmont Ave', now())",
            propertyId, orgId);
        foreach (var tenant in tenants)
        {
            await InsertAsync(conn, tx, ct,
                "INSERT INTO tenants (id, org_id, display_name, lifecycle_status, created_at) VALUES (@id, @org, 'Refund test tenant', 'current', now())",
                tenant, orgId);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, string sql,
        Guid id, Guid orgId)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("org", orgId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>The refund-check ledger, passed through, counting the status reads Payments makes.</summary>
internal sealed class CountingRefundLedger(IRefundCheckLedger inner) : IRefundCheckLedger
{
    private int _statusReads;

    public int StatusReads => Volatile.Read(ref _statusReads);

    public Task<IReadOnlyList<RefundableFunds>> GetRefundableAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken ct) =>
        inner.GetRefundableAsync(tenantIds, ct);

    public Task<RefundPosting> PostAsync(RefundPostingRequest request, CancellationToken ct) => inner.PostAsync(request, ct);

    public Task<Guid> VoidAsync(Guid entryId, string reason, CancellationToken ct) => inner.VoidAsync(entryId, reason, ct);

    public Task<IReadOnlyDictionary<Guid, RefundLedgerStatus>> GetStatusesAsync(IReadOnlyCollection<Guid> entryIds,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _statusReads);
        return inner.GetStatusesAsync(entryIds, ct);
    }
}
