using System.Text.RegularExpressions;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The one interface from architecture tests to repository files. It owns root discovery, safe
/// relative-path resolution, generated-directory exclusion, line diagnostics, and whole-line
/// comment handling for C#, SQL, and YAML.
/// </summary>
internal sealed class RepositorySource
{
    private static readonly Lazy<RepositorySource?> Located = new(Locate);
    private static readonly HashSet<string> CodeExtensions =
        new([".cs", ".sql"], StringComparer.OrdinalIgnoreCase);

    private readonly string root;

    private RepositorySource(string root)
    {
        this.root = root;
    }

    public static RepositorySource Current =>
        TryLocate()
        ?? throw new InvalidOperationException("LeaseBook.slnx not found above the test base directory.");

    public static RepositorySource? TryLocate() => Located.Value;

    public RepositoryFile File(string relativePath)
    {
        var (fullPath, normalized) = Resolve(relativePath);
        if (!System.IO.File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Repository file not found: {normalized}", fullPath);
        }

        return new RepositoryFile(fullPath, normalized);
    }

    public IReadOnlyList<RepositoryFile> CodeFilesUnder(params string[] relativeRoots)
    {
        var files = new List<RepositoryFile>();
        foreach (var relativeRoot in relativeRoots)
        {
            var (fullRoot, normalizedRoot) = Resolve(relativeRoot);
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException($"Repository directory not found: {normalizedRoot}");
            }

            files.AddRange(
                Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
                    .Where(path => CodeExtensions.Contains(Path.GetExtension(path)))
                    .Where(path => !IsGenerated(path))
                    .Select(path => new RepositoryFile(path, Path.GetRelativePath(root, path))));
        }

        return [.. files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)];
    }

    private (string FullPath, string Normalized) Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Repository paths must be relative.", nameof(relativePath));
        }

        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        var rootedPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Repository paths may not escape the repository root.", nameof(relativePath));
        }

        return (fullPath, Path.GetRelativePath(root, fullPath));
    }

    private static bool IsGenerated(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase) ||
               segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static RepositorySource? Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "LeaseBook.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null ? null : new RepositorySource(directory.FullName);
    }
}

internal sealed class RepositoryFile(string fullPath, string relativePath)
{
    private readonly Lazy<string> text = new(() => System.IO.File.ReadAllText(fullPath));

    public string RelativePath { get; } = relativePath;

    public string Text => text.Value;

    public IReadOnlyList<RepositoryLine> Lines =>
        [
            .. Text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select((line, index) => new RepositoryLine(RelativePath, index + 1, line)),
        ];

    public string SignificantText => string.Join('\n', Lines.Where(line => line.Code.Length > 0).Select(line => line.Text));

    public IReadOnlyList<RepositoryMatch> Find(Regex pattern) =>
        [
            .. Lines.Where(line => pattern.IsMatch(line.Code))
                .Select(line => new RepositoryMatch(line.RelativePath, line.Number, line.Text.Trim())),
        ];
}

internal readonly record struct RepositoryLine(string RelativePath, int Number, string Text)
{
    public string Code
    {
        get
        {
            var marker = Path.GetExtension(RelativePath).ToLowerInvariant() switch
            {
                ".cs" => "//",
                ".sql" => "--",
                ".yaml" or ".yml" => "#",
                _ => null,
            };

            return marker is not null && Text.TrimStart().StartsWith(marker, StringComparison.Ordinal)
                ? ""
                : Text;
        }
    }
}

internal readonly record struct RepositoryMatch(string RelativePath, int Number, string Text)
{
    public override string ToString() => $"{RelativePath}:{Number}: {Text}";
}
