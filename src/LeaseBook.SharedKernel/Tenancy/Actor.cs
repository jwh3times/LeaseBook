using System.Text.RegularExpressions;

namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// Who is accountable for a unit of work. Either a signed-in user, or a named system process — and
/// the caller must say which.
/// <para>
/// <b>Actor is durable attribution, not a runtime declaration (ADR-039).</b> Every actor renders to a
/// <see cref="Reference"/> — <c>user:&lt;guid&gt;</c> or <c>system:&lt;process&gt;</c> — and that
/// reference is persisted alongside every audited write and every journal entry. Before ADR-039 a
/// system actor stamped a null <c>created_by</c> / <c>actor_user_id</c> and the reason lived only at
/// the call site, so a historical row could not say whether it came from the demo seeder, the nightly
/// sweep, a CLI verb, or an accidental omission. It can now.
/// </para>
/// <para>
/// A null actor is no longer a way to write. It is only a way to read a row that predates ADR-039.
/// </para>
/// </summary>
public sealed record Actor
{
    /// <summary>
    /// A stable process identifier, not a sentence. Lower-case segments joined by <c>.</c>, <c>:</c>,
    /// <c>_</c> or <c>-</c>, so <c>seed:demo</c> and <c>invariant-sweep</c> are process names an
    /// auditor can group by, while "ran during the nightly maintenance window" is not.
    /// <para>
    /// The shape is enforced because the value is now durable: a free-text reason is fine at a call
    /// site and useless in a column, since two spellings of the same process no longer aggregate.
    /// </para>
    /// </summary>
    private static readonly Regex ProcessName = new(
        @"^[a-z0-9]+(?:[.:_-][a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>The longest process name the <c>actor_process</c> columns accept.</summary>
    public const int MaxProcessLength = 64;

    private Actor(Guid? userId, string? process)
    {
        UserId = userId;
        Process = process;
    }

    /// <summary>The signed-in user, or <see langword="null"/> for a system actor.</summary>
    public Guid? UserId { get; }

    /// <summary>The system process that acted, or <see langword="null"/> when a user did.</summary>
    public string? Process { get; }

    /// <summary>True when no human is accountable for this work.</summary>
    public bool IsSystem => UserId is null;

    /// <summary><c>user</c> or <c>system</c> — the persisted discriminator.</summary>
    public string Kind => IsSystem ? SystemKind : UserKind;

    /// <summary>
    /// The durable form written to <c>actor_kind</c> + the table's user or process column, and the
    /// one string an audit read can group or filter on.
    /// </summary>
    public string Reference => IsSystem ? $"{SystemKind}:{Process}" : $"{UserKind}:{UserId}";

    /// <summary>Persisted value of <see cref="Kind"/> for a human actor.</summary>
    public const string UserKind = "user";

    /// <summary>Persisted value of <see cref="Kind"/> for a system actor.</summary>
    public const string SystemKind = "system";

    /// <summary>A signed-in user, identified by the id the host resolved from their claims.</summary>
    public static Actor User(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                "A user actor needs a real user id. If no user is accountable for this work, say so " +
                "with Actor.System(process) rather than passing an empty id.", nameof(userId));
        }

        return new Actor(userId, process: null);
    }

    /// <summary>
    /// The system, acting as the named <paramref name="process"/> — a seeder, a scheduled job, a CLI
    /// verb. The name is persisted, so it must be the stable identifier of the process rather than a
    /// description of the occasion.
    /// </summary>
    public static Actor System(string process)
    {
        if (string.IsNullOrWhiteSpace(process))
        {
            throw new ArgumentException(
                "A system actor needs a process name — the point of this type is that an " +
                "unattributed write cannot be written by accident.", nameof(process));
        }

        if (process.Length > MaxProcessLength)
        {
            throw new ArgumentException(
                $"A system process name must fit the actor_process column ({MaxProcessLength} chars); " +
                $"'{process}' is {process.Length}.", nameof(process));
        }

        if (!ProcessName.IsMatch(process))
        {
            throw new ArgumentException(
                $"'{process}' is not a stable process identifier. Use lower-case segments joined by " +
                ". : _ or - (for example 'seed:demo' or 'invariant-sweep'). This value is persisted, " +
                "so a free-text reason would leave the audit trail unable to group two writes by the " +
                "same process.", nameof(process));
        }

        return new Actor(userId: null, process);
    }

    public override string ToString() => Reference;
}
