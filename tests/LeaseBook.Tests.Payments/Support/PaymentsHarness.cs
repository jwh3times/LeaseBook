using LeaseBook.Modules.Accounting;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LeaseBook.Tests.Payments.Support;

/// <summary>
/// One organization with a payment fixture, and the two payment engines built over it, with no host.
/// <para>
/// The engines run on the real schema as the app role, because they take advisory locks, read under
/// RLS and rely on the database's keys. Their three ports are stand-ins the test controls: the
/// ledger records what Payments asked to post (<see cref="RecordingLedger"/>), and eligibility and the
/// fee rules answer what the test says. What the ledger then does with a request is Accounting's, and
/// <c>LeaseBook.Tests.Accounting</c> tests it; what is asserted here is the decision Payments made.
/// </para>
/// <para>
/// The journal ids Payments stores have foreign keys to <c>journal_entries</c>, so the recording
/// ledger posts a real, minimal entry for each one through Accounting's public
/// <see cref="IAccountingEvents"/>, resolved from a container holding only the Accounting module over
/// this scope's context. No journal row is written by hand.
/// </para>
/// </summary>
internal sealed class PaymentsHarness : IAsyncDisposable
{
    private readonly ServiceProvider _accounting;

    private PaymentsHarness(OrgScope scope, ServiceProvider accounting, FixtureBinding binding, Guid userId,
        Guid ownerId, IReadOnlyList<Guid> tenants)
    {
        Scope = scope;
        _accounting = accounting;
        Binding = binding;
        UserId = userId;
        OwnerId = ownerId;
        Tenants = tenants;
        Ledger = new RecordingLedger(accounting.GetRequiredService<IAccountingEvents>(), ownerId, binding.BankId);
        Payments = new PaymentEngine(scope.Db, scope.Tenant, Clock, Ledger, Eligibility, FeeRules);
        Settlements = new SettlementEngine(scope.Db, Clock, Payments, Ledger);
    }

    public OrgScope Scope { get; }

    public FixtureBinding Binding { get; }

    /// <summary>The signed-in user every payment is submitted as.</summary>
    public Guid UserId { get; }

    /// <summary>The owner the recording ledger's stand-in entries are posted for.</summary>
    public Guid OwnerId { get; }

    public IReadOnlyList<Guid> Tenants { get; }

    public ManualClock Clock { get; } = new();

    public RecordingLedger Ledger { get; }

    public StubEligibility Eligibility { get; } = new();

    public StubFeeRules FeeRules { get; } = new();

    public PaymentEngine Payments { get; }

    public SettlementEngine Settlements { get; }

    /// <summary>The processor's reference the tests give an operation when it is accepted.</summary>
    public static string ProviderOf(PaymentOperation op) => "prov_" + op.Id.ToString("N");

    public static async Task<PaymentsHarness> CreateAsync(PostgresFixture fixture, CancellationToken ct, int tenants = 2,
        string mode = PaymentModes.Simulation)
    {
        var scope = await OrgScope.CreateAsync(fixture, ct);
        var bankId = UuidV7.NewId();
        var ownerId = UuidV7.NewId();
        var tenantIds = Enumerable.Range(0, tenants).Select(_ => UuidV7.NewId()).ToArray();
        var binding = new FixtureBinding(scope.OrgId, UuidV7.NewId(), bankId, "acct_" + UuidV7.NewId().ToString("N"))
        {
            Mode = mode,
        };

        var services = new ServiceCollection().AddAccountingModule();
        services.AddSingleton<DbContext>(scope.Db);
        services.AddSingleton<IOrgContext>(scope.Tenant);
        services.AddSingleton<IActorContext>(scope.Actor);
        var accounting = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });

        await scope.RunAsync(() => accounting.GetRequiredService<IChartOfAccounts>().ProvisionAsync(
            [new BankAccountSpec(bankId, "Operating Trust", BankPurpose.Trust)], ct), ct);
        await SeedDirectoryAsync(fixture, scope.OrgId, bankId, ownerId, tenantIds, ct);

        var userId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            var email = $"payer-{userId:N}@payments.test";
            migratorDb.Users.Add(new AppUser { Id = userId, OrgId = scope.OrgId, UserName = email, Email = email });
            await migratorDb.SaveChangesAsync(ct);
        }

        await scope.RunAsync(async () =>
        {
            scope.Db.Add(new PaymentFixture { Id = binding.Generation, BankId = bankId, Account = binding.Account });
            await scope.Db.SaveChangesAsync(ct);
        }, ct);
        scope.Db.ChangeTracker.Clear();

        return new PaymentsHarness(scope, accounting, binding, userId, ownerId, tenantIds);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one organization transaction with nothing tracked from earlier
    /// work, as each request and each worker step in the host gets a context of its own.
    /// </summary>
    public async Task<T> InOrg<T>(Func<Task<T>> work, CancellationToken ct)
    {
        T result = default!;
        Scope.Db.ChangeTracker.Clear();
        try
        {
            await Scope.RunAsync(async () => result = await work(), ct);
        }
        finally
        {
            Scope.Db.ChangeTracker.Clear();
        }
        return result;
    }

    public Task InOrg(Func<Task> work, CancellationToken ct) => InOrg(async () => { await work(); return true; }, ct);

    /// <summary>Submits a payment and has the processor accept it, as the worker does: claim, then complete.</summary>
    public async Task<PaymentOperation> AcceptedAsync(decimal amount, CancellationToken ct, int tenant = 0,
        string method = PaymentMethods.Ach, decimal fee = 0m)
    {
        FeeRules.Fee = fee;
        var op = await InOrg(() => Payments.SubmitAsync(Binding, Tenants[tenant], UserId, UuidV7.NewId(), amount, "USD",
            method, fee, ct), ct);
        await JudgeAsync(op.Id, ct, accept: ProviderOf(op));
        return await ReadAsync(op.Id, ct);
    }

    /// <summary>
    /// What the worker does when an operation is due: claims it and completes it with the processor's
    /// acceptance. Returns false when it was not claimable.
    /// </summary>
    public async Task<bool> JudgeAsync(Guid id, CancellationToken ct, string? accept = null)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        var claimed = await InOrg(() => Payments.ClaimAsync(Binding, id, ct), ct);
        if (claimed is null) { return false; }
        var provider = accept ?? claimed.ProviderId ?? throw new InvalidOperationException("Accept the operation first.");
        await InOrg(() => Payments.CompleteAsync(Binding, id, claimed.LeaseClaimId!.Value,
            new ProcessorResult("Accepted", provider), ct), ct);
        return true;
    }

    /// <summary>Stores a processor's observation about a payment, as a delivered notice is stored.</summary>
    public Task ObserveAsync(ProcessorObservation observation, CancellationToken ct) =>
        InOrg(() => Payments.ReceiveAsync(Binding, observation, ct), ct);

    /// <summary>Stores an observation and lets the worker judge it.</summary>
    public async Task EmitAsync(ProcessorObservation observation, CancellationToken ct, Guid? operation = null)
    {
        await ObserveAsync(observation, ct);
        var id = operation ?? (await InOrg(() => Scope.Db.Set<PaymentOperation>().AsNoTracking()
            .SingleAsync(x => x.ProviderId == observation.ProviderId, ct), ct)).Id;
        await JudgeAsync(id, ct);
    }

    public Task<PaymentOperation> ReadAsync(Guid id, CancellationToken ct) =>
        InOrg(() => Scope.Db.Set<PaymentOperation>().AsNoTracking().SingleAsync(x => x.Id == id, ct), ct);

    public Task<PaymentSettlement> ReadSettlementAsync(Guid id, CancellationToken ct) =>
        InOrg(() => Scope.Db.Set<PaymentSettlement>().AsNoTracking().SingleAsync(x => x.Id == id, ct), ct);

    public Task<List<PaymentSettlementItem>> ReadItemsAsync(Guid id, CancellationToken ct) =>
        InOrg(() => Scope.Db.Set<PaymentSettlementItem>().AsNoTracking().Where(x => x.SettlementId == id)
            .OrderBy(x => x.Item).ToListAsync(ct), ct);

    public Task<List<PaymentEffect>> ReadEffectsAsync(CancellationToken ct) =>
        InOrg(() => Scope.Db.Set<PaymentEffect>().AsNoTracking().ToListAsync(ct), ct);

    public Task<List<PaymentObservation>> ReadObservationsAsync(CancellationToken ct) =>
        InOrg(() => Scope.Db.Set<PaymentObservation>().AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct), ct);

    /// <summary>
    /// A processor's word about one payment, in the shape the simulator emits: a bank credit is the
    /// clean item (the charge, the quoted fee, the ledger amount net); every other kind is fee-free.
    /// </summary>
    public ProcessorObservation Observation(PaymentOperation op, string kind, string? eventId = null) => new(
        eventId ?? $"{op.Id:N}:{kind}", ProviderOf(op), Binding.Account, Binding.Mode, Binding.Generation, kind,
        kind == "BankCredit" ? op.ChargedAmount : op.Amount, kind == "BankCredit" ? op.QuotedFee : 0m, op.Amount,
        "USD", Binding.BankId, Today, "evidence-" + op.Id.ToString("N"), "payout-" + op.Id.ToString("N"), true,
        Clock.GetUtcNow().UtcDateTime);

    public DateOnly Today => DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);

    /// <summary>Payout evidence with the bank's word, dated today on this fixture's bank.</summary>
    public ProcessorSettlement Payout(string payoutId, decimal bankAmount, params ProcessorSettlementItem[] items) => new(
        payoutId, Binding.Account, Binding.Mode, Binding.Generation, SettlementPayoutTypes.Standard, bankAmount, "USD",
        Binding.BankId, Today, "bank-" + payoutId, Clock.GetUtcNow().UtcDateTime, items);

    /// <summary>A processor's own account of a payout, with no bank's word (ADR-054).</summary>
    public ProcessorPayout ProcessorPayout(string payoutId, decimal amount, params ProcessorSettlementItem[] items) => new(
        payoutId, Binding.Account, Binding.Mode, Binding.Generation, SettlementPayoutTypes.Standard,
        ProcessorPayoutStates.Paid, amount, "USD", Binding.BankId, Clock.GetUtcNow().UtcDateTime, items);

    public BankEvidence Bank(string payoutId, decimal amount, DateOnly? date = null, string? evidenceId = null) => new(
        payoutId, Binding.Account, Binding.Mode, Binding.Generation, amount, date ?? Today, evidenceId ?? "bank-" + payoutId);

    public static ProcessorSettlementItem Line(string item, string kind, PaymentOperation op, decimal gross, decimal fee,
        decimal net) => new(item, kind, ProviderOf(op), gross, fee, net, "USD");

    public static ProcessorSettlementItem FeeLine(string item, decimal fee) =>
        new(item, SettlementItemKinds.Fee, "", 0m, fee, -fee, "USD");

    /// <summary>Stores payout evidence and checks it, as the host does when a payout notice arrives.</summary>
    public async Task<PaymentSettlement> SettleAsync(ProcessorSettlement evidence, CancellationToken ct, Guid? confirmedBy = null)
    {
        var id = await InOrg(() => Settlements.ReceiveAsync(Binding, evidence, ct), ct)
            ?? throw new InvalidOperationException("The payout evidence was not well formed.");
        return (await InOrg(() => Settlements.EvaluateAsync(Binding, id, confirmedBy, ct), ct))!;
    }

    public ValueTask DisposeAsync()
    {
        _accounting.Dispose();
        return Scope.DisposeAsync();
    }

    // The directory rows the payment tables and the stand-in entries have foreign keys to, written as
    // the migrator under this org's context (the directory tables force RLS even for their owner), as
    // the accounting harness writes them. They are key targets only; nothing here reads them.
    private static async Task SeedDirectoryAsync(PostgresFixture fixture, Guid orgId, Guid bankId, Guid ownerId,
        IReadOnlyList<Guid> tenants, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);

        await InsertAsync(conn, tx, ct,
            "INSERT INTO bank_accounts (id, org_id, name, purpose, created_at) VALUES (@id, @org, 'Operating Trust', 'trust', now())",
            bankId, orgId);
        await InsertAsync(conn, tx, ct,
            "INSERT INTO owners (id, org_id, name, created_at) VALUES (@id, @org, 'Payments test owner', now())",
            ownerId, orgId);
        foreach (var tenant in tenants)
        {
            await InsertAsync(conn, tx, ct,
                "INSERT INTO tenants (id, org_id, display_name, lifecycle_status, created_at) VALUES (@id, @org, 'Payments test tenant', 'current', now())",
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

/// <summary>A clock the test moves. Starts at the real time, so rows stamped by the database agree with it.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
