using Microsoft.Extensions.Configuration;

namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// File-system implementation of <see cref="IArtifactStore"/> for development and integration
/// tests. Writes to <c>Reporting:ArtifactDirectory</c> from configuration (falls back to the
/// system temp directory so tests work without any configuration).
/// <para>
/// Replace with an Azure Blob / Azurite-backed implementation in the deployed environment (M8).
/// </para>
/// </summary>
public sealed class LocalArtifactStore : IArtifactStore
{
    private readonly string _directory;

    public LocalArtifactStore(IConfiguration configuration)
    {
        var configured = configuration["Reporting:ArtifactDirectory"];
        _directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "leasebook-artifacts")
            : configured;

        Directory.CreateDirectory(_directory);
    }

    public async Task PutAsync(byte[] bytes, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrEmpty(key);

        var path = ArtifactPath(key);
        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var path = ArtifactPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, ct);
    }

    private string ArtifactPath(string key) =>
        Path.Combine(_directory, SanitizeKey(key));

    /// <summary>
    /// Strips path separators from the key so a caller cannot path-traverse outside the store
    /// directory. Keys should be UUID-v7 strings; this is a belt-and-suspenders guard.
    /// </summary>
    private static string SanitizeKey(string key) =>
        key.Replace('/', '_').Replace('\\', '_').Replace("..", "__");
}
