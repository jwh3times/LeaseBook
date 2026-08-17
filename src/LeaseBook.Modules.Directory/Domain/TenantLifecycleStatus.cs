namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// The tenant party's operational lifecycle. Financial standing is derived from Accounting and is
/// never stored in this enum.
/// </summary>
public enum TenantLifecycleStatus
{
    Current,
    Evicting,
    Past,
}
