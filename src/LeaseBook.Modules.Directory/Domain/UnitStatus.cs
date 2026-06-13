namespace LeaseBook.Modules.Directory.Domain;

/// <summary>Occupancy state of a rentable unit (§C.1). Persisted as snake_case text under a DB CHECK.</summary>
public enum UnitStatus
{
    Occupied,
    Vacant,
    Unavailable,
}
