using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;

namespace LeaseBook.SharedKernel.Csv;

/// <summary>
/// The bound on every CSV import. Each one takes the file as a JSON string and parses it in memory,
/// so the limit is explicit rather than whatever the server's default body size happens to be.
/// </summary>
public static class CsvImportLimits
{
    /// <summary>
    /// 5 MiB of text — tens of thousands of rows, well beyond a full portfolio's entity or balance
    /// export or years of a bank account's statement lines. Enforced by each import's validation, which
    /// is what gives the operator a message naming the limit.
    /// </summary>
    public const int MaxCharacters = 5 * 1024 * 1024;

    /// <summary>
    /// The request-body limit on the import endpoints, enforced by the server before the body is read.
    /// Larger than <see cref="MaxCharacters"/> because the file travels JSON-encoded — quotes and line
    /// breaks are escaped, and non-ASCII text takes more than one byte — so a file at the character
    /// limit must still fit.
    /// </summary>
    public const long MaxRequestBytes = 16L * 1024 * 1024;

    public const string TooLargeMessage = "The file is too large to import. The limit is 5 MB.";

    /// <summary>Applies <see cref="MaxRequestBytes"/> to an import endpoint.</summary>
    public static TBuilder WithCsvImportLimit<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new RequestBodyLimit(MaxRequestBytes));

    private sealed record RequestBodyLimit(long? MaxRequestBodySize) : IRequestSizeLimitMetadata;
}
