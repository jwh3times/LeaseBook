using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using Shouldly;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// TDD unit tests for <see cref="MgmtFee.Compute"/> (ADR-018) and the preview math of
/// <see cref="DisbursementRunStrategy"/>. Pure in-memory — no DB, no Testcontainers.
/// </summary>
public sealed class DisbursementTests
{
    // ── MgmtFee.Compute ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(2100, 800, 168.00)]   // 2100 × 800/10000 = 168.00 (brief canonical case)
    [InlineData(1000, 1000, 100.00)]  // exact 10%
    [InlineData(1000, 333, 33.30)]    // 1000 × 333/10000 = 33.3 → 33.30
    [InlineData(999, 1, 0.10)]        // 999 × 1/10000 = 0.0999 → 0.10 (AwayFromZero)
    [InlineData(100, 0, 0.00)]        // bps=0 → 0
    public void Compute_rounds_half_away_from_zero(decimal equity, int bps, decimal expected)
    {
        MgmtFee.Compute(equity, bps).ShouldBe(expected);
    }

    [Fact]
    public void Compute_returns_zero_when_bps_is_null()
    {
        MgmtFee.Compute(100m, null).ShouldBe(0m);
    }

    [Fact]
    public void Compute_returns_zero_when_equity_is_zero()
    {
        MgmtFee.Compute(0m, 800).ShouldBe(0m);
    }

    // ── Preview math (using fake ports) ──────────────────────────────────────

    [Fact]
    public async Task Preview_eligible_owner_produces_correct_fee_net_disburse()
    {
        // equity=2100, bps=800, reserve=200
        // fee = Round(2100 × 800/10000, 2, AwayFromZero) = 168.00
        // netBeforeReserve = 2100 - 168 = 1932.00
        // disburse = 1932 - 200 = 1732.00
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var strategy = BuildStrategy(
            owners: [new OwnerDisbursementRow(ownerId, "Alice", ReserveAmount: 200m, DefaultMgmtFeeBps: 800)],
            equity: [(ownerId, 2100m)]);

        var preview = await strategy.PreviewAsync(new RunPeriod(2026, 6), ct);

        preview.Rows.Count.ShouldBe(1);
        var row = preview.Rows[0];
        row.TargetKind.ShouldBe(RunTargetKind.Owner);
        row.TargetId.ShouldBe(ownerId);
        row.ExcludedReason.ShouldBeNull();
        row.Amount.ShouldBe(1732.00m, "disburse = equity - fee - reserve = 2100 - 168 - 200 = 1732");
        row.Detail["fee"].ShouldBe("168.00");
        row.Detail["netBeforeReserve"].ShouldBe("1932.00");
        row.Detail["reserve"].ShouldBe("200.00");
    }

    [Fact]
    public async Task Preview_owner_below_reserve_floor_is_excluded_with_reason()
    {
        // equity=150, bps=800, reserve=200
        // fee=12.00, net=138.00, disburse=138-200=-62 → excluded
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var strategy = BuildStrategy(
            owners: [new OwnerDisbursementRow(ownerId, "Bob", ReserveAmount: 200m, DefaultMgmtFeeBps: 800)],
            equity: [(ownerId, 150m)]);

        var preview = await strategy.PreviewAsync(new RunPeriod(2026, 6), ct);

        preview.Rows.Count.ShouldBe(1);
        var row = preview.Rows[0];
        row.ExcludedReason.ShouldBe("below_reserve_floor");
        row.Amount.ShouldBe(0m);
    }

    [Fact]
    public async Task Preview_zero_equity_owner_is_excluded_non_positive_equity()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var strategy = BuildStrategy(
            owners: [new OwnerDisbursementRow(ownerId, "Carol", ReserveAmount: 0m, DefaultMgmtFeeBps: 800)],
            equity: [(ownerId, 0m)]);

        var preview = await strategy.PreviewAsync(new RunPeriod(2026, 6), ct);

        preview.Rows[0].ExcludedReason.ShouldBe("non_positive_equity");
    }

    [Fact]
    public async Task Preview_negative_equity_owner_is_excluded_non_positive_equity()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var strategy = BuildStrategy(
            owners: [new OwnerDisbursementRow(ownerId, "Dave", ReserveAmount: 0m, DefaultMgmtFeeBps: 800)],
            equity: [(ownerId, -50m)]);

        var preview = await strategy.PreviewAsync(new RunPeriod(2026, 6), ct);

        preview.Rows[0].ExcludedReason.ShouldBe("non_positive_equity");
    }

    [Fact]
    public async Task Preview_owner_with_null_bps_gets_zero_fee_and_full_disburse()
    {
        // fee=0, disburse = equity - 0 - reserve = 1000 - 0 - 100 = 900
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var strategy = BuildStrategy(
            owners: [new OwnerDisbursementRow(ownerId, "Eve", ReserveAmount: 100m, DefaultMgmtFeeBps: null)],
            equity: [(ownerId, 1000m)]);

        var preview = await strategy.PreviewAsync(new RunPeriod(2026, 6), ct);

        var row = preview.Rows[0];
        row.ExcludedReason.ShouldBeNull();
        row.Amount.ShouldBe(900m);
        row.Detail["fee"].ShouldBe("0.00");
    }

    [Fact]
    public async Task Preview_empty_org_returns_empty_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var strategy = BuildStrategy(owners: [], equity: []);
        var preview = await strategy.PreviewAsync(new RunPeriod(2026, 6), ct);
        preview.Rows.Count.ShouldBe(0);
        preview.Exceptions.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Preview_owner_with_no_equity_journal_activity_treated_as_zero()
    {
        // Owner has no journal lines → not in equityMap → default 0 → excluded non_positive_equity
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var strategy = BuildStrategy(
            owners: [new OwnerDisbursementRow(ownerId, "Frank", ReserveAmount: 0m, DefaultMgmtFeeBps: 800)],
            equity: []); // no equity entry

        var preview = await strategy.PreviewAsync(new RunPeriod(2026, 6), ct);

        preview.Rows[0].ExcludedReason.ShouldBe("non_positive_equity");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DisbursementRunStrategy BuildStrategy(
        IReadOnlyList<OwnerDisbursementRow> owners,
        IReadOnlyList<(Guid OwnerId, decimal Equity)> equity)
    {
        return new DisbursementRunStrategy(
            new StubOwnerData(owners),
            new StubEquityBalances(equity.ToDictionary(e => e.OwnerId, e => e.Equity)),
            new StubBankInfo(),
            new StubPostedRefs());
    }
}

// ── stubs ─────────────────────────────────────────────────────────────────────

file sealed class StubOwnerData(IReadOnlyList<OwnerDisbursementRow> rows) : IOwnerDisbursementData
{
    public Task<IReadOnlyList<OwnerDisbursementRow>> GetAsync(CancellationToken ct) =>
        Task.FromResult(rows);
}

file sealed class StubEquityBalances(Dictionary<Guid, decimal> map) : IOwnerEquityBalances
{
    public Task<IReadOnlyDictionary<Guid, decimal>> GetAsync(
        IReadOnlyList<Guid> ownerIds, string basis, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, decimal>>(
            ownerIds.Where(map.ContainsKey).ToDictionary(id => id, id => map[id]));
}

file sealed class StubBankInfo : IBankAccountInfo
{
    public Task<(Guid OperatingBankId, string Display)> GetOperatingTrustAsync(CancellationToken ct) =>
        Task.FromResult((Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa"), "Test Trust Bank"));
}

file sealed class StubPostedRefs : IPostedSourceRefs
{
    public Task<IReadOnlySet<string>> GetExistingAsync(IReadOnlyList<string> keys, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}
