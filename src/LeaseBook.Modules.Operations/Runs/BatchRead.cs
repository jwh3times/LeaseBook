namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// Calls a batch port only when there is something to ask it about (F-b).
/// <para>
/// Every cross-module read in a run strategy is a batch port returning a set or a map keyed by id
/// (ADR-007: "ports expose batch reads that return maps, never per-id reads"). Each call site guarded
/// itself against an empty key list with the same three-line ternary and the same cast, seven times
/// across the three strategies. The guard is not an optimisation detail worth restating — it is the
/// single rule that a run with nothing selected asks nothing, and it reads better named than inlined.
/// </para>
/// <para>
/// Distinct names rather than overloads: the two shapes differ only in the lambda's return type, and
/// overload resolution driven by an inferred lambda return is exactly the kind of thing that starts
/// resolving to the wrong member when someone adds a third shape.
/// </para>
/// </summary>
internal static class BatchRead
{
    /// <summary>
    /// The set the port returns for <paramref name="keys"/>, or an empty one without calling it.
    /// </summary>
    public static Task<IReadOnlySet<TResult>> SetOrEmptyAsync<TKey, TResult>(
        IReadOnlyList<TKey> keys,
        Func<IReadOnlyList<TKey>, Task<IReadOnlySet<TResult>>> read)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(read);

        return keys.Count > 0
            ? read(keys)
            : Task.FromResult<IReadOnlySet<TResult>>(new HashSet<TResult>());
    }

    /// <summary>
    /// The map the port returns for <paramref name="keys"/>, or an empty one without calling it.
    /// </summary>
    public static Task<IReadOnlyDictionary<TKey, TValue>> MapOrEmptyAsync<TKey, TValue>(
        IReadOnlyList<TKey> keys,
        Func<IReadOnlyList<TKey>, Task<IReadOnlyDictionary<TKey, TValue>>> read)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(read);

        return keys.Count > 0
            ? read(keys)
            : Task.FromResult<IReadOnlyDictionary<TKey, TValue>>(new Dictionary<TKey, TValue>());
    }
}
