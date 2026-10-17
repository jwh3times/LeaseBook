using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// The unmatched-observation inbox (#491): processor notices kept more than ten minutes that name a
/// payment reference no payment here carries. Staff see how many, and the newest of them with their
/// age. The count and the list are one definition, so they cannot disagree.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class UnmatchedObservationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_notice_no_payment_answers_for_is_counted_and_listed_once_it_is_more_than_ten_minutes_old()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var matched = await h.AcceptedAsync(100m, ct);
        var other = await h.AcceptedAsync(40m, ct);
        var now = h.Clock.GetUtcNow().UtcDateTime;

        // A notice for a payment held here, however old, is that payment's.
        await h.StoreObservationAsync(h.Observation(matched, "Succeeded"), now.AddMinutes(-30), ct);
        var oldest = await h.StoreObservationAsync(Stray(h, other, "u1", 11m), now.AddMinutes(-30), ct);
        var tiedA = await h.StoreObservationAsync(Stray(h, other, "u2", 22m), now.AddMinutes(-20), ct);
        var tiedB = await h.StoreObservationAsync(Stray(h, other, "u3", 33m), now.AddMinutes(-20), ct);
        // Exactly ten minutes old is not yet more than ten, and five is too recent: either may be about to match.
        await h.StoreObservationAsync(Stray(h, other, "u4", 44m), now.AddMinutes(-10), ct);
        await h.StoreObservationAsync(Stray(h, other, "u5", 55m), now.AddMinutes(-5), ct);

        (await h.QueryAsync(new GetUnmatchedPaymentObservations(), ct)).ShouldBe(3);

        var list = await h.QueryAsync(new GetUnmatchedPaymentObservationList(), ct);

        // Newest first; two stored at one instant come later id first, as the database orders ids.
        var tied = string.CompareOrdinal(tiedA.Id.ToString(), tiedB.Id.ToString()) > 0 ? new[] { tiedA, tiedB } : [tiedB, tiedA];
        list.ShouldBe([
            new UnmatchedObservationView(tied[0].Id, now.AddMinutes(-20), "Succeeded", tied[0].Gross, "USD", tied[0].ProviderId, 20),
            new UnmatchedObservationView(tied[1].Id, now.AddMinutes(-20), "Succeeded", tied[1].Gross, "USD", tied[1].ProviderId, 20),
            new UnmatchedObservationView(oldest.Id, now.AddMinutes(-30), "Succeeded", 11m, "USD", "prov_stray_u1", 30),
        ]);
    }

    [Fact]
    public async Task An_organization_with_nothing_unmatched_counts_none_and_lists_none()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.StoreObservationAsync(h.Observation(op, "Succeeded"), h.Clock.GetUtcNow().UtcDateTime.AddHours(-1), ct);

        (await h.QueryAsync(new GetUnmatchedPaymentObservations(), ct)).ShouldBe(0);
        (await h.QueryAsync(new GetUnmatchedPaymentObservationList(), ct)).ShouldBeEmpty();
    }

    // A notice in the shape the processor sends, about a payment reference nothing here carries.
    private static ProcessorObservation Stray(PaymentsHarness h, PaymentOperation template, string name, decimal gross) =>
        h.Observation(template, "Succeeded", "stray-" + name) with { ProviderId = "prov_stray_" + name, Gross = gross, Net = gross };
}
