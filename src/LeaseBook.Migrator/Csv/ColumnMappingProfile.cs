namespace LeaseBook.Migrator.Csv;

/// <summary>One canonical field and the source headers that may carry it (first match wins).</summary>
public sealed record FieldMapping(string Canonical, string[] CandidateHeaders, bool Required);

/// <summary>A named mapping from a CSV's headers to canonical field names. Pure data (spec §6).</summary>
public sealed record ColumnMappingProfile(IReadOnlyList<FieldMapping> Fields)
{
    /// <summary>Resolves canonical → actual header against a CSV's header row (case/space-insensitive).</summary>
    public IReadOnlyDictionary<string, string> Resolve(IReadOnlyList<string> headers, out IReadOnlyList<RowError> missing)
    {
        static string Norm(string s) => s.Trim().ToLowerInvariant();
        var byNorm = headers.ToDictionary(Norm, h => h);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var miss = new List<RowError>();
        foreach (var f in Fields)
        {
            var hit = f.CandidateHeaders.Select(Norm).FirstOrDefault(byNorm.ContainsKey);
            if (hit is not null) resolved[f.Canonical] = byNorm[hit];
            else if (f.Required) miss.Add(new RowError(0, f.Canonical, $"required column not found (looked for: {string.Join(", ", f.CandidateHeaders)})"));
        }
        missing = miss;
        return resolved;
    }
}
