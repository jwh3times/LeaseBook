using LeaseBook.Modules.Directory.Domain;

namespace LeaseBook.Modules.Directory.Features.Shared;

/// <summary>The single home for the system-row exclusion (m2_retro #2). Every directory <b>LINQ</b>
/// roster/list read funnels through <c>NotSystem()</c> so a new read can't silently leak aggregate rows.
/// Raw-SQL reads (e.g. <c>Search</c>) are the explicit exception — they hand-code <c>WHERE NOT is_system</c>
/// and are covered by the behavioral <c>SystemRowExclusionTests</c> instead.</summary>
public static class SystemFilter
{
    public static IQueryable<T> NotSystem<T>(this IQueryable<T> source)
        where T : class, ISystemFlagged
        => source.Where(e => !e.IsSystem);
}
