namespace LeaseBook.Modules.Directory.Domain;

/// <summary>Tenant standing shown on the index/detail screens (§C.1). snake_case text under a DB CHECK.</summary>
public enum TenantStatus
{
    Current,
    Late,
    Prepaid,
    Evicting,
    Past,
}
