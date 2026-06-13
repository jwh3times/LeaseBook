namespace LeaseBook.Modules.Directory.Features.Shared;

/// <summary>
/// The consistent list envelope (§C.3 / P42): <c>{ items, total, page, pageSize }</c>. The server stays
/// paginated for future scale; at demo/Pro scale the UI loads one ample page and filters client-side.
/// </summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>
/// Normalized list query params (§C.3): <c>page</c> 1-based (default 1), <c>pageSize</c> default 50 /
/// max 200, optional free-text <c>q</c> and <c>sort</c> (<c>field[:asc|desc]</c>). Mixed into each list
/// query so every list shares the same contract.
/// </summary>
public readonly record struct PageParams(int Page, int PageSize, string? Q, string? Sort)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static PageParams Normalize(int? page, int? pageSize, string? q, string? sort) => new(
        Page: page is > 0 ? page.Value : 1,
        PageSize: Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize),
        Q: string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
        Sort: string.IsNullOrWhiteSpace(sort) ? null : sort.Trim());

    public int Skip => (Page - 1) * PageSize;

    /// <summary>Parses <c>field[:asc|desc]</c>; descending when the suffix is <c>:desc</c>.</summary>
    public (string Field, bool Descending) ParseSort(string defaultField)
    {
        if (Sort is null)
        {
            return (defaultField, false);
        }

        var parts = Sort.Split(':', 2);
        var field = string.IsNullOrWhiteSpace(parts[0]) ? defaultField : parts[0].Trim();
        var descending = parts.Length == 2 && parts[1].Trim().Equals("desc", StringComparison.OrdinalIgnoreCase);
        return (field, descending);
    }
}
