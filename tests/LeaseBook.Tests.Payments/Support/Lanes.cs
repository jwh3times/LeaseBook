using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Tests.Common;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Tests.Payments.Support;

/// <summary>
/// A connection of its own to the harness's organization (<see cref="OrgLane{TLane}"/>), with the two
/// engines and a recording ledger over it.
/// </summary>
internal sealed class Lane : OrgLane<Lane>
{
    private readonly ServiceProvider _accounting;
    private readonly Lazy<ServiceProvider> _refundChecks;

    public Lane(PostgresFixture fixture, Guid orgId, Guid bankId, Guid ownerId, TimeProvider clock,
        IPaymentEligibility eligibility, IPaymentFeeRules feeRules)
        : base(fixture, orgId)
    {
        _accounting = PaymentsHarness.AccountingOver(Db, Org, Actor);
        _refundChecks = new(() => RefundCheckHarness.ServicesOver(Db, Org, Actor, clock));
        Ledger = new RecordingLedger(_accounting.GetRequiredService<IAccountingEvents>(), ownerId, bankId);
        Payments = new PaymentEngine(Db, Org, clock, Ledger, eligibility, feeRules);
        Settlements = new SettlementEngine(Db, clock, Payments, Ledger);
    }

    /// <summary>A lane for refund-check work, which uses none of the payment engines' stand-ins.</summary>
    public Lane(PostgresFixture fixture, Guid orgId, TimeProvider clock)
        : this(fixture, orgId, Guid.Empty, Guid.Empty, clock, new StubEligibility(), new StubFeeRules())
    {
    }

    /// <summary>
    /// The refund-check commands and queries over this lane's connection, with the real host ledger
    /// adapter and Accounting behind them (see <see cref="RefundCheckHarness.ServicesOver"/>).
    /// </summary>
    public ISender Sender => _refundChecks.Value.GetRequiredService<ISender>();

    public PaymentEngine Payments { get; }

    public SettlementEngine Settlements { get; }

    public RecordingLedger Ledger { get; }

    /// <summary>
    /// Holds, uncommitted, a notice row under <paramref name="eventId"/> as if another delivery of it
    /// were being stored. A <see cref="PaymentEngine.ReceiveAsync"/> of that event takes its event and
    /// reference locks and then waits at its own insert for this one to end: it stands still at the
    /// point between storing a fact and waking the payment, which nothing else can hold it at.
    /// </summary>
    public HeldWork<bool> HoldNotice(FixtureBinding binding, string eventId, CancellationToken ct) => Hold(async lane =>
    {
        lane.Db.Add(new PaymentObservation
        {
            Id = UuidV7.NewId(),
            EventId = eventId,
            ProviderId = "held-notice",
            Generation = binding.Generation,
            Account = binding.Account,
            Mode = binding.Mode,
            Kind = "Processing",
            BankId = binding.BankId,
            ObservedAt = DateTime.UtcNow,
            Fingerprint = new string('0', 64),
        });
        await lane.Db.SaveChangesAsync(ct);
        return true;
    }, ct);

    public override async ValueTask DisposeAsync()
    {
        if (_refundChecks.IsValueCreated) { await _refundChecks.Value.DisposeAsync(); }
        await _accounting.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>What became of two units of work run by <see cref="PaymentsHarness.RaceAsync{T1, T2}"/>.</summary>
internal sealed record Race<T1, T2>(T1 First, T2? Second, Exception? SecondError, RecordingLedger FirstLedger, RecordingLedger SecondLedger)
{
    public int SettlementRequests => FirstLedger.Settlements.Count + SecondLedger.Settlements.Count;
}
