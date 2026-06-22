namespace LeaseBook.Modules.Directory.Domain;

/// <summary>Directory entities that carry a synthetic-aggregate flag (P40). Roster/search reads must
/// exclude system rows via <c>NotSystem()</c> so aggregate roll-ups never leak (m2_retro #2).</summary>
public interface ISystemFlagged
{
    bool IsSystem { get; }
}
