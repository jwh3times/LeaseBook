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
/// A late fee names the rent charge it assesses (<c>assesses_entry_id</c>), and the posting service
/// holds that link to what it means: the target exists, is a rent charge, and is assessed by a late fee
/// on the same tenant, property, owner and unit, at most once. A retry of the same assessment is a
/// duplicate source reference, which callers treat as already done, not a second assessment.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class LateFeeAssessmentTests(PostgresFixture fixture)
{
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private readonly Guid _unit = UuidV7.NewId();
    private readonly Guid _tenant = UuidV7.NewId();

    // A second of each dimension, for a fee that names the wrong one.
    private readonly Guid _otherOwner = UuidV7.NewId();
    private readonly Guid _otherProperty = UuidV7.NewId();
    private readonly Guid _otherUnit = UuidV7.NewId();
    private readonly Guid _otherTenant = UuidV7.NewId();

    [Fact]
    public async Task A_late_fee_on_its_rent_charge_posts_linked_to_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        var rent = await PostAsync(scope, Rent(), ct);

        var fee = await PostAsync(scope, LateFee(rent, "late:feb"), ct);

        var entry = await EntryAsync(scope, fee, ct);
        (entry.EventType, entry.EventSubtype, entry.AssessesEntryId).ShouldBe(("FeeCharged", "late", (Guid?)rent));
    }

    [Fact]
    public async Task A_late_fee_on_an_entry_that_does_not_exist_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        var missing = UuidV7.NewId();

        var ex = await Should.ThrowAsync<EntryNotFoundException>(() => PostAsync(scope, LateFee(missing, "late:feb"), ct));

        ex.EntryId.ShouldBe(missing);
    }

    [Fact]
    public async Task A_late_fee_may_assess_only_a_rent_charge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        var otherFee = await PostAsync(scope, new FeeCharged(
            _tenant, _property, _owner, _unit, new Money(75m), D(1), FeeKind.MaintenanceRecharge, "recharge"), ct);

        var ex = await Should.ThrowAsync<InvalidAssessmentTargetException>(() =>
            PostAsync(scope, LateFee(otherFee, "late:feb"), ct));

        ex.EntryId.ShouldBe(otherFee);
    }

    [Fact]
    public async Task Only_a_late_fee_may_assess_a_rent_charge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        var rent = await PostAsync(scope, Rent(), ct);

        var ex = await Should.ThrowAsync<InvalidAssessmentTargetException>(() => PostAsync(scope,
            LateFee(rent, "other:feb") with { Kind = FeeKind.Other }, ct));

        ex.EntryId.ShouldBe(rent);
        (await AssessmentsOfAsync(scope, rent, ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("property")]
    [InlineData("owner")]
    [InlineData("unit")]
    public async Task A_late_fee_on_another_tenancy_than_its_rent_charge_is_rejected(string differs)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        var rent = await PostAsync(scope, Rent(), ct);
        var fee = LateFee(rent, "late:feb");
        fee = differs switch
        {
            "tenant" => fee with { TenantId = _otherTenant },
            "property" => fee with { PropertyId = _otherProperty },
            "owner" => fee with { OwnerId = _otherOwner },
            _ => fee with { UnitId = _otherUnit },
        };

        var ex = await Should.ThrowAsync<InvalidAssessmentTargetException>(() => PostAsync(scope, fee, ct));

        ex.EntryId.ShouldBe(rent);
        (await AssessmentsOfAsync(scope, rent, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_late_fee_entry_with_no_receivable_line_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        var rent = await PostAsync(scope, Rent(), ct);

        // Balanced, and late by its subtype, but it charges no tenant: there is nothing to compare with the rent.
        var ex = await Should.ThrowAsync<InvalidAssessmentTargetException>(() => scope.RunAsync(() =>
            Posting(scope).PostAsync(new PostEntryRequest(
                D(6), "FeeCharged", "late", "late fee", "late:feb",
                [
                    new PostLineRequest(AccountCodes.OwnerEquity, new Money(50m), null, EntryBasis.Accrual,
                        PropertyId: _property, OwnerId: _owner),
                    new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(50m), EntryBasis.Accrual,
                        PropertyId: _property, OwnerId: _owner),
                ],
                AssessesEntryId: rent), ct), ct));

        ex.EntryId.ShouldBe(rent);
    }

    [Fact]
    public async Task A_second_late_fee_on_an_assessed_rent_charge_names_the_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        var rent = await PostAsync(scope, Rent(), ct);
        var first = await PostAsync(scope, LateFee(rent, "late:feb"), ct);

        var ex = await Should.ThrowAsync<RentObligationAlreadyAssessedException>(() =>
            PostAsync(scope, LateFee(rent, "late:feb:again"), ct));

        (ex.RentObligationEntryId, ex.ExistingEntryId).ShouldBe((rent, first));
    }

    [Fact]
    public async Task A_refused_second_assessment_leaves_the_callers_unsaved_work_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        var rent = await PostAsync(scope, Rent(), ct);
        await PostAsync(scope, LateFee(rent, "late:feb"), ct);

        // The refusal is read before anything is saved, so the caller's context is as it left it. Were it
        // left to the unique index, the posting's save would carry the caller's pending row into a failed
        // insert, and the backstop's ChangeTracker.Clear would then discard it.
        await scope.RunAsync(async () =>
        {
            scope.Db.Add(BankReconciliation.Start(scope.TrustBankId, 2026, 3, new Money(0m)));
            await Should.ThrowAsync<RentObligationAlreadyAssessedException>(() =>
                Events(scope).PostAsync(LateFee(rent, "late:feb:again"), ct));
            await scope.Db.SaveChangesAsync(ct);
        }, ct);

        var kept = false;
        await scope.RunAsync(async () => kept = await scope.Db.Set<BankReconciliation>()
            .AnyAsync(r => r.BankAccountId == scope.TrustBankId && r.PeriodYear == 2026 && r.PeriodMonth == 3, ct), ct);
        kept.ShouldBeTrue();
    }

    [Fact]
    public async Task Retrying_the_same_late_fee_is_a_duplicate_source_ref_not_a_second_assessment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);
        var rent = await PostAsync(scope, Rent(), ct);
        var first = await PostAsync(scope, LateFee(rent, "late:feb"), ct);

        // A late-fee run that runs again: the source reference says this exact assessment was posted.
        var ex = await Should.ThrowAsync<DuplicateSourceRefException>(() => PostAsync(scope, LateFee(rent, "late:feb"), ct));

        (ex.SourceRef, ex.ExistingEntryId).ShouldBe(("late:feb", first));
    }

    private Task<OrgScope> ScopeAsync(CancellationToken ct) => ProvisionedScopeAsync(
        fixture, ct,
        owners: [_owner, _otherOwner], tenants: [_tenant, _otherTenant],
        properties: [_property, _otherProperty], units: [_unit, _otherUnit]);

    private static async Task<Guid> PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task<JournalEntry> EntryAsync(OrgScope scope, Guid id, CancellationToken ct)
    {
        JournalEntry entry = null!;
        await scope.RunAsync(async () => entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == id, ct), ct);
        return entry;
    }

    private static async Task<int> AssessmentsOfAsync(OrgScope scope, Guid rent, CancellationToken ct)
    {
        var count = 0;
        await scope.RunAsync(async () => count = await scope.Db.Set<JournalEntry>().CountAsync(e => e.AssessesEntryId == rent, ct), ct);
        return count;
    }

    private RentCharged Rent() => new(_tenant, _property, _owner, _unit, new Money(1000m), D(1), "Feb rent");

    private FeeCharged LateFee(Guid rent, string sourceRef) =>
        new(_tenant, _property, _owner, _unit, new Money(50m), D(6), FeeKind.Late, "late fee", sourceRef, AssessesEntryId: rent);

    private static DateOnly D(int day) => new(2026, 2, day);
}
