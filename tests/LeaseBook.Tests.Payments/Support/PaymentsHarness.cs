using LeaseBook.Modules.Accounting;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
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
/// <para>
/// The payment and payout commands and queries under <c>Features/</c> are dispatched through the real
/// pipeline (validation, then the handler) by <see cref="Sender"/>, over these same engines, so a
/// staff list or a review action is answered by the code the host runs, with the same stand-ins
/// behind it.
/// </para>
/// </summary>
internal sealed class PaymentsHarness : IAsyncDisposable
{
    private readonly ServiceProvider _accounting;
    private readonly ServiceProvider _features;
    private readonly PostgresFixture _fixture;

    private PaymentsHarness(PostgresFixture fixture, OrgScope scope, ServiceProvider accounting, FixtureBinding binding, Guid userId,
        Guid ownerId, IReadOnlyList<Guid> tenants)
    {
        _fixture = fixture;
        Scope = scope;
        _accounting = accounting;
        Binding = binding;
        UserId = userId;
        OwnerId = ownerId;
        Tenants = tenants;
        Ledger = new RecordingLedger(accounting.GetRequiredService<IAccountingEvents>(), ownerId, binding.BankId);
        Payments = new PaymentEngine(scope.Db, scope.Tenant, Clock, Ledger, Eligibility, FeeRules);
        Settlements = new SettlementEngine(scope.Db, Clock, Payments, Ledger);
        _features = FeaturesOver(scope.Db, scope.Tenant, scope.Actor, Clock, Payments, Settlements);
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

    /// <summary>The dispatcher over <see cref="Scope"/>'s context. Outside <see cref="InOrg{T}"/> it has no organization.</summary>
    public ISender Sender => _features.GetRequiredService<ISender>();

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

        var accounting = AccountingOver(scope.Db, scope.Tenant, scope.Actor);

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

        return new PaymentsHarness(fixture, scope, accounting, binding, userId, ownerId, tenantIds);
    }

    // Only the Accounting module, over one context: what the recording ledger posts its stand-in entries through.
    internal static ServiceProvider AccountingOver(DbContext db, IOrgContext org, IActorContext actor)
    {
        var services = new ServiceCollection().AddAccountingModule();
        services.AddSingleton(db);
        services.AddSingleton(org);
        services.AddSingleton(actor);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
    }

    /// <summary>
    /// Every handler and validator of Payments behind the real dispatcher, over one context and the
    /// engines given, as the host composes them for a request.
    /// </summary>
    internal static ServiceProvider FeaturesOver(DbContext db, IOrgContext org, IActorContext actor, TimeProvider clock,
        PaymentEngine payments, SettlementEngine settlements)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLeaseBookCqrs(typeof(LeaseBook.Modules.Payments.ModuleMarker).Assembly);
        services.AddSingleton(db);
        services.AddSingleton(org);
        services.AddSingleton(actor);
        services.AddSingleton(clock);
        services.AddSingleton(payments);
        services.AddSingleton(settlements);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
    }

    /// <summary>
    /// A second connection to this organization, with engines of its own over it and the same clock
    /// and stand-ins, for running two pieces of work at once (see <see cref="RaceAsync"/>).
    /// </summary>
    public Lane Lane() => new(_fixture, Scope.OrgId, Binding.BankId, OwnerId, Clock, Eligibility, FeeRules);

    /// <summary>
    /// Runs <paramref name="first"/> to the end of its work on one connection and holds its
    /// transaction open; starts <paramref name="second"/> on another; waits until the second has
    /// either finished or is waiting on a lock held against it; and only then commits the first.
    /// <para>
    /// That makes the interleaving the same every time, whichever way the code goes. Where the
    /// second takes a lock the first holds, it waits there and reads what the first committed. Where
    /// it takes none, it reads the state as it was before the first, and goes on either to finish
    /// on that or to wait at a row or key the first wrote, which it then writes over or collides
    /// with. Neither is decided by timing: nothing sleeps for the other to get anywhere.
    /// </para>
    /// </summary>
    public async Task<Race<T1, T2>> RaceAsync<T1, T2>(Func<Lane, Task<T1>> first, Func<Lane, Task<T2>> second, CancellationToken ct)
    {
        await using var a = Lane();
        await using var b = Lane();
        var held = a.Hold(first, ct);
        var firstResult = await held.Done;
        var running = b.RunAsync(second, ct);
        await WaitingOrDoneAsync(b, running, ct);
        held.Commit();
        await held.Ended();
        var (secondResult, error) = await Lanes.OutcomeOf(running);
        Scope.Db.ChangeTracker.Clear();
        return new(firstResult, secondResult, error, a.Ledger, b.Ledger);
    }

    /// <inheritdoc cref="Lanes.WaitingOrDoneAsync"/>
    public Task<bool> WaitingOrDoneAsync(Lane lane, Task running, CancellationToken ct) =>
        Lanes.WaitingOrDoneAsync(_fixture, lane, running, ct);

    /// <summary>
    /// Points the organization's fixture at another account, or at a new bank, by editing the row as
    /// the migrator, and returns the binding that now matches it. Every row made before keeps the
    /// account and bank it was made under. The generation cannot change: every payment and payout
    /// holds a key to it, and an organization has one fixture.
    /// <para>
    /// No code path can change a fixture row: <c>PaymentFixtureBootstrap.SeedAsync</c> writes one per
    /// organization, and <c>RevokeAppendOnly</c> takes UPDATE from the app role. Only a hand edit
    /// reaches this state, and the tests that use it pin the engines' defensive guards against it.
    /// </para>
    /// </summary>
    public async Task<FixtureBinding> RebindAsync(CancellationToken ct, string? account = null, bool newBank = false)
    {
        var bankId = newBank ? UuidV7.NewId() : Binding.BankId;
        await using var conn = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, Scope.OrgId, ct);
        if (newBank)
        {
            await InsertAsync(conn, tx, ct,
                "INSERT INTO bank_accounts (id, org_id, name, purpose, created_at) VALUES (@id, @org, 'Second Trust', 'trust', now())",
                bankId, Scope.OrgId);
        }
        await using (var update = new NpgsqlCommand(
            "UPDATE payment_fixtures SET bank_id = @bank, account = @account WHERE id = @id", conn, tx))
        {
            update.Parameters.AddWithValue("bank", bankId);
            update.Parameters.AddWithValue("account", account ?? Binding.Account);
            update.Parameters.AddWithValue("id", Binding.Generation);
            if (await update.ExecuteNonQueryAsync(ct) != 1) { throw new InvalidOperationException("The fixture was not rebound."); }
        }
        await tx.CommitAsync(ct);
        return Binding with { BankId = bankId, Account = account ?? Binding.Account };
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

    /// <summary>Sends a command through <see cref="Sender"/> in one organization transaction, as a request does.</summary>
    public Task<T> SendAsync<T>(ICommand<T> command, CancellationToken ct) => InOrg(() => Sender.Send(command, ct), ct);

    /// <summary>Runs a query through <see cref="Sender"/> in one organization transaction, as a request does.</summary>
    public Task<T> QueryAsync<T>(IQuery<T> query, CancellationToken ct) => InOrg(() => Sender.Query(query, ct), ct);

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

    /// <summary>
    /// Sets when a payment or a payout was stored. The context stamps a new row with the wall clock as
    /// it saves, so two rows are never stored at one instant; a test that means to stand on a tie, or
    /// on an age, sets the time here, as the app role, which may update both tables.
    /// </summary>
    public Task RestampAsync<T>(Guid id, DateTime createdAt, CancellationToken ct) where T : class => InOrg(async () =>
    {
        var row = await Scope.Db.Set<T>().SingleAsync(x => EF.Property<Guid>(x, "Id") == id, ct);
        Scope.Db.Entry(row).Property("CreatedAt").CurrentValue = createdAt;
        await Scope.Db.SaveChangesAsync(ct);
    }, ct);

    /// <summary>
    /// Stores a processor's observation as <see cref="PaymentEngine.ReceiveAsync"/> stores a new one,
    /// field for field, but stored at <paramref name="storedAt"/>. Observations are append-only for the
    /// app role, so a stored time a test stands on is given when the row is written, never after.
    /// Nothing is judged and no payment is woken: this is for reads of what is held.
    /// </summary>
    public Task<PaymentObservation> StoreObservationAsync(ProcessorObservation o, DateTime storedAt, CancellationToken ct) => InOrg(async () =>
    {
        var row = new PaymentObservation
        {
            Id = UuidV7.NewId(),
            EventId = o.EventId,
            ProviderId = o.ProviderId,
            Generation = o.Generation,
            Account = o.Account,
            Mode = o.Mode,
            Kind = o.Kind,
            Gross = o.Gross,
            Fee = o.Fee,
            Net = o.Net,
            Currency = o.Currency,
            BankId = o.BankId,
            BankDate = o.BankDate,
            EvidenceId = o.EvidenceId,
            PayoutId = o.PayoutId,
            Complete = o.Complete,
            ObservedAt = o.ObservedAt,
            CreatedAt = storedAt,
            Fingerprint = PaymentEngine.Hash(System.Text.Json.JsonSerializer.Serialize(o)),
        };
        Scope.Db.Add(row);
        await Scope.Db.SaveChangesAsync(ct);
        return row;
    }, ct);

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

    public async ValueTask DisposeAsync()
    {
        await _features.DisposeAsync();
        await _accounting.DisposeAsync();
        await Scope.DisposeAsync();
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

/// <summary>
/// A clock the test moves. Starts at the real time, so rows stamped by the database agree with it,
/// in whole microseconds, which is what the database keeps: a time read back from a row is then
/// exactly the time the clock gave, and a test can stand on a boundary instead of a tick beside it.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
