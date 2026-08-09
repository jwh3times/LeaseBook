namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// Who is accountable for a unit of work. Either a signed-in user, or the system with a named
/// reason — and the caller must say which, because the two are not distinguishable afterwards from
/// the row alone.
/// <para>
/// A system actor stamps a null <c>created_by</c> / <c>actor_user_id</c>, exactly as an unattributed
/// write did before this type existed. The difference is not in the data, it is in the call site: a
/// nullable id lets "deliberately nobody" and "nobody set this" look identical in the source, and
/// that ambiguity is what let a seeder quietly hand-write the actor context to work around a
/// signature that could not carry one. The <see cref="Reason"/> is required so the answer to "which
/// system process wrote this?" lives somewhere, even while the schema has no column for it.
/// </para>
/// </summary>
public sealed record Actor
{
    private Actor(Guid? userId, string? reason)
    {
        UserId = userId;
        Reason = reason;
    }

    /// <summary>The signed-in user, or <see langword="null"/> for a system actor.</summary>
    public Guid? UserId { get; }

    /// <summary>Why the system acted, or <see langword="null"/> when a user did.</summary>
    public string? Reason { get; }

    /// <summary>True when no human is accountable for this work.</summary>
    public bool IsSystem => UserId is null;

    /// <summary>A signed-in user, identified by the id the host resolved from their claims.</summary>
    public static Actor User(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                "A user actor needs a real user id. If no user is accountable for this work, say so " +
                "with Actor.System(reason) rather than passing an empty id.", nameof(userId));
        }

        return new Actor(userId, reason: null);
    }

    /// <summary>
    /// The system, acting for the named <paramref name="reason"/> — a seeder, a scheduled job, a CLI
    /// verb. Use a short stable identifier for the process, not a sentence.
    /// </summary>
    public static Actor System(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A system actor needs a reason naming the process acting — the point of this type is " +
                "that an unattributed write cannot be written by accident.", nameof(reason));
        }

        return new Actor(userId: null, reason);
    }

    public override string ToString() => UserId is { } id ? $"user:{id}" : $"system:{Reason}";
}
