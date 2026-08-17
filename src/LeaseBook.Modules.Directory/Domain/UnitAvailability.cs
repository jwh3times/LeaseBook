namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// Whether a unit can be offered or used operationally. Occupancy is derived from the lease effective
/// on a date and is intentionally independent of this value.
/// </summary>
public enum UnitAvailability
{
    Available,
    Unavailable,
}
