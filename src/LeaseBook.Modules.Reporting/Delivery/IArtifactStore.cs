namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// Write-once artifact store for immutable PDF bytes. The local implementation writes to a
/// configured directory; the deployed implementation writes to Azure Blob / Azurite behind the
/// same interface (M8 / ADR to be filed when ACS is wired).
/// <para>
/// Keys are opaque strings. <see cref="PutAsync"/> is idempotent: a second put with the same key
/// overwrites the existing bytes (last-writer-wins). Callers should use a collision-resistant key
/// (e.g. a UUID-v7 delivery id) to avoid accidental overwrites.
/// </para>
/// </summary>
public interface IArtifactStore
{
    /// <summary>Stores <paramref name="bytes"/> under <paramref name="key"/>.</summary>
    Task PutAsync(byte[] bytes, string key, CancellationToken ct);

    /// <summary>
    /// Retrieves the bytes stored under <paramref name="key"/>.
    /// Returns null when no artifact exists for that key.
    /// </summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct);
}
