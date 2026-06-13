namespace LeaseBook.Modules.Directory.Domain;

/// <summary>Lifecycle of a <see cref="LeaseLite"/> (§C.1). snake_case text under a DB CHECK.</summary>
public enum LeaseStatus
{
    Active,
    Ended,
    Pending,
}
