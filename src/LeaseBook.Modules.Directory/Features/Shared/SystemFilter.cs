using LeaseBook.Modules.Directory.Domain;

namespace LeaseBook.Modules.Directory.Features.Shared;

/// <summary>The single home for the system-row exclusion (m2_retro #2). Every directory roster/list
/// read funnels through <c>NotSystem()</c> so a new read can't silently leak aggregate rows.</summary>
public static class SystemFilter
{
    public static IQueryable<T> NotSystem<T>(this IQueryable<T> source)
        where T : class, ISystemFlagged
        => source.Where(e => !e.IsSystem);
}
