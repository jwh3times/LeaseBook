namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// The projection that turns an attempt's append-only event history into its one current delivery
/// status (ADR-040). This is the only sanctioned way to answer "what happened to this send?" — there
/// is no status column to read instead, by design.
/// </summary>
public static class DeliveryStatus
{
    /// <summary>
    /// The current status of <paramref name="attempt"/>: the kind of its highest-sequence event.
    /// Requires <c>Events</c> to be loaded.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The attempt has no events. Every attempt is created together with its <c>Queued</c> event, so
    /// an empty history means the events were not loaded — silently reporting <c>Queued</c> for that
    /// would be indistinguishable from a genuinely pending send.
    /// </exception>
    public static DeliveryEventKind Of(StatementDeliveryAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return Of(attempt.Events, attempt.Id);
    }

    /// <summary>
    /// The current status implied by <paramref name="events"/> — one attempt's events, in any order.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sequence is empty.</exception>
    public static DeliveryEventKind Of(
        IEnumerable<StatementDeliveryEvent> events, Guid attemptId = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        // Highest Sequence, not latest OccurredAt: a provider webhook can arrive late carrying an
        // early timestamp, and ordering on the provider's clock would let it overwrite a status we
        // already recorded. See StatementDeliveryEvent.Sequence.
        var latest = events.MaxBy(e => e.Sequence)
            ?? throw new InvalidOperationException(
                $"Delivery attempt {attemptId} has no events. Every attempt is created with a " +
                "Queued event, so this means the event history was not loaded.");

        return latest.Kind;
    }
}
