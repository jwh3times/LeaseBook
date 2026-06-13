namespace LeaseBook.Tests.Common;

/// <summary>Cookie-to-header XSRF helpers for the test client (mirrors what the SPA does).</summary>
public static class ApiClientExtensions
{
    /// <summary>GETs /api/auth/csrf and echoes the resulting XSRF-TOKEN as the X-XSRF-TOKEN header.</summary>
    public static async Task PrimeCsrfAsync(this HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync("/api/auth/csrf", ct);
        var token = ExtractCookie(response, "XSRF-TOKEN")
            ?? throw new InvalidOperationException("CSRF endpoint did not set the XSRF-TOKEN cookie.");
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
    }

    public static string? ExtractCookie(this HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return null;
        }

        var prefix = name + "=";
        foreach (var cookie in setCookies)
        {
            if (cookie.StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = cookie[prefix.Length..];
                var end = value.IndexOf(';');
                return Uri.UnescapeDataString(end >= 0 ? value[..end] : value);
            }
        }

        return null;
    }
}
