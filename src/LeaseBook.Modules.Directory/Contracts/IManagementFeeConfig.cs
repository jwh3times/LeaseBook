namespace LeaseBook.Modules.Directory.Contracts;

/// <summary>
/// Resolves the effective management-fee rate for an owner/property from the stored config columns
/// (P44): the property override (<c>properties.mgmt_fee_bps</c>) wins, else the owner default
/// (<c>owners.default_mgmt_fee_bps</c>); null if neither is set. **M2 stores and resolves only — no fee
/// math** (that, plus its rounding ADR, is M6). This reads Directory's own tables (no cross-module
/// boundary); M6 (Operations) will consume it later through its <i>own</i> port + host adapter (P49).
/// </summary>
public interface IManagementFeeConfig
{
    Task<int?> GetEffectiveFeeBpsAsync(Guid ownerId, Guid? propertyId, CancellationToken ct);
}
