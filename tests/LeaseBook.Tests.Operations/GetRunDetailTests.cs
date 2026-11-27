using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Features.Runs;
using LeaseBook.Tests.Common;
using Shouldly;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// The run-detail read refuses an item whose stored reason contradicts its outcome (#573). Every run
/// since M6 records a reason on each skipped or excluded item and none on a posted one, so a row that
/// breaks that was not written by the run engine; showing it would put a reason in front of an
/// operator that the run never gave, or hide one it did.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class GetRunDetailTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(RunItemStatus.Skipped, "{}")]
    [InlineData(RunItemStatus.Excluded, """{"equity":0}""")]
    [InlineData(RunItemStatus.Posted, """{"reason":"rent_zero"}""")]
    public async Task An_item_whose_reason_contradicts_its_outcome_is_refused(RunItemStatus status, string snapshot)
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = await OrgScope.CreateAsync(fixture, ct);
        await using var _ = scope;

        var run = BulkRun.Create(RunType.Rent, 2026, 6, "{}", DateTime.UtcNow);
        await scope.RunAsync(async () =>
        {
            scope.Db.Add(run);
            scope.Db.Add(BulkRunItem.Create(
                run.Id, RunTargetKind.Lease, Guid.NewGuid(), status, 0m, snapshot, run.CreatedAt));
            await scope.Db.SaveChangesAsync(ct);
        }, ct);

        var handler = new GetRunDetailHandler(scope.Db, new NoNames());
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(
            () => handler.Handle(new GetRunDetail(run.Id), ct), ct));
        refused.Message.ShouldContain($"is {status} but its snapshot");
    }

    [Fact]
    public async Task An_item_that_agrees_with_its_outcome_is_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = await OrgScope.CreateAsync(fixture, ct);
        await using var _ = scope;

        var run = BulkRun.Create(RunType.Rent, 2026, 6, "{}", DateTime.UtcNow);
        await scope.RunAsync(async () =>
        {
            scope.Db.Add(run);
            scope.Db.Add(BulkRunItem.Create(
                run.Id, RunTargetKind.Lease, Guid.NewGuid(), RunItemStatus.Skipped, 0m,
                """{"reason":"already_charged_in_period"}""", run.CreatedAt));
            await scope.Db.SaveChangesAsync(ct);
        }, ct);

        RunDetail? detail = null;
        await scope.RunAsync(
            async () => detail = await new GetRunDetailHandler(scope.Db, new NoNames())
                .Handle(new GetRunDetail(run.Id), ct),
            ct);

        var item = detail.ShouldNotBeNull().Items.ShouldHaveSingleItem();
        (item.Outcome, item.Reason, item.TargetName).ShouldBe(
            (RunItemStatus.Skipped, RunReason.AlreadyChargedInPeriod, (string?)null));
    }

    /// <summary>A Directory that knows no one: every target reads as not found.</summary>
    private sealed class NoNames : IRunTargetNames
    {
        public Task<IReadOnlyDictionary<Guid, RunLeaseTarget>> GetLeaseNamesAsync(
            IReadOnlyList<Guid> leaseIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, RunLeaseTarget>>(new Dictionary<Guid, RunLeaseTarget>());

        public Task<IReadOnlyDictionary<Guid, string>> GetOwnerNamesAsync(
            IReadOnlyList<Guid> ownerIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }
}
