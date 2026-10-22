using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features.RefundChecks;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Two pieces of refund-check work at the same moment. Issue, void and print take one lock per
/// organization before anything else, so a retried key and a check number are checked against what the
/// other has committed, and a print never lands on a check being voided. Each race holds the first open
/// at the end of its work and lets the second run until it finishes or waits (see
/// <see cref="RefundCheckHarness.RaceAsync{T1, T2}"/>), so the outcome is the one the locks make.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RefundCheckConcurrencyTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Two_submissions_of_one_issue_at_once_make_one_check()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        var request = h.Issue(1043);

        var race = await h.RaceAsync(lane => lane.Sender.Send(request, ct), lane => lane.Sender.Send(request, ct), ct);

        race.SecondError.ShouldBeNull();
        race.Second!.Id.ShouldBe(race.First.Id);
        (await h.QueryAsync(new GetRefundChecks(), ct)).Total.ShouldBe(1);
        (await h.QueryAsync(new GetRefundCheckOptions(h.Tenants[0]), ct)).Funds.ShouldHaveSingleItem().Held.ShouldBe(1350m);
    }

    [Fact]
    public async Task Two_checks_with_one_number_on_one_bank_at_once_leave_the_second_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);

        var race = await h.RaceAsync(lane => lane.Sender.Send(h.Issue(1043), ct), lane => lane.Sender.Send(h.Issue(1043), ct), ct);

        race.SecondError.ShouldBeOfType<RefundCheckConflictException>().Code.ShouldBe("check_number_taken");
        (await h.QueryAsync(new GetRefundChecks(), ct)).Total.ShouldBe(1);
        (await h.QueryAsync(new GetRefundCheckOptions(h.Tenants[0]), ct)).Funds.ShouldHaveSingleItem().Held.ShouldBe(1350m);
    }

    [Fact]
    public async Task A_print_asked_for_while_the_check_is_voided_is_refused_once_the_void_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        var issued = await h.SendAsync(h.Issue(1043), ct);

        var race = await h.RaceAsync(
            lane => lane.Sender.Send(new VoidIssuedRefundCheck(issued.Id, "Misprinted"), ct),
            lane => lane.Sender.Send(new RecordRefundCheckPrint(issued.Id), ct), ct);

        race.SecondError.ShouldBeOfType<RefundCheckConflictException>().Code.ShouldBe("refund_check_voided");
        var check = (await h.QueryAsync(new GetRefundChecks(Id: issued.Id), ct)).Items.ShouldHaveSingleItem();
        (check.Status, check.PrintCount).ShouldBe(("voided", 0));
    }

    [Fact]
    public async Task A_number_another_transaction_is_inserting_is_refused_when_it_commits_and_the_refund_is_rolled_back()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await RefundCheckHarness.CreateAsync(fixture, ct);
        await h.HoldDepositAsync(1450m, ct);
        var otherEntry = await h.HoldPrepaymentAsync(75m, ct);

        // Every issue takes the refund-check lock first, so the number check always sees a committed rival
        // and the unique index behind it is a backstop. A row inserted without the lock reaches the
        // backstop: no code path does that, so the row is written by hand and held uncommitted.
        await using var holder = h.Lane();
        var held = holder.Hold(async lane =>
        {
            lane.Db.Add(new RefundCheck
            {
                Id = UuidV7.NewId(),
                Key = UuidV7.NewId(),
                TenantId = h.Tenants[0],
                BankAccountId = h.DepositBankId,
                CheckNumber = 1043,
                Source = "prepayment",
                Amount = 75m,
                IssueDate = RefundCheckHarness.Issued,
                PayeeName = "Jasmine Carter",
                AddressLine1 = "412 Oakmont Ave",
                City = "Asheville",
                State = "NC",
                PostalCode = "28801",
                EntryId = otherEntry,
            });
            await lane.Db.SaveChangesAsync(ct);
            return true;
        }, ct);
        await held.Done;

        await using var issuer = h.Lane();
        var issuing = issuer.RunAsync(lane => lane.Sender.Send(h.Issue(1043), ct), ct);
        // The issue has posted its refund and stands at the index, waiting on the held row's key.
        (await OrgLanes.WaitingOrDoneAsync(fixture, issuer, issuing, ct)).ShouldBeTrue();
        held.Commit();
        await held.Ended();

        var (_, error) = await OrgLanes.OutcomeOf(issuing);
        error.ShouldBeOfType<RefundCheckConflictException>().Code.ShouldBe("check_number_taken");
        (await h.QueryAsync(new GetRefundCheckOptions(h.Tenants[0]), ct)).Funds
            .Single(f => f.Source == "deposit").Held.ShouldBe(1450m);
    }
}
