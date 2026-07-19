using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using LeaseBook.Web.Seeding;

namespace LeaseBook.Web.Cli;

/// <summary>
/// The WP-9 latency harness: measures p50/p95/p99 on the four money-critical read paths (tenant
/// ledger, dashboard, bank register, owner statement) against an <b>already-running</b> host and the
/// <see cref="LoadSeeder"/> org, and fails when p95 misses the budget. The p95 &lt; 300 ms figure is
/// declared in <c>docs/perf.md</c>; ADR-016 supplies the ~300-unit scale, not a latency number.
/// <para>
/// The owner statement is probed deliberately: ADR-016's revisit trigger is worded about
/// <c>GetOwnerStatementData</c> contributing to page-load time, so a probe that skipped statements
/// could say nothing about that trigger.
/// </para>
/// <para>
/// Deliberately an over-the-wire HTTP client rather than an in-process query timer: the budget is a
/// user-facing promise, so the number has to include routing, authorization, serialization, and the
/// RLS transaction — not just the SQL. It follows the SPA's own auth sequence (prime the antiforgery
/// cookie, log in, echo it as a header) because <c>ApiAntiforgeryMiddleware</c> validates every
/// unsafe request; a probe that skipped it would 400 on login.
/// </para>
/// <para>
/// Deliberately <b>not</b> a CI gate initially: runner hardware variance would make it flaky, and
/// the number is only meaningful on comparable hardware. It is a documented, repeatable local check
/// (see <c>docs/perf.md</c>); revisit CI-gating once a deployed environment exists (Track B).
/// </para>
/// </summary>
public static class PerfProbe
{
    private const int DefaultIterations = 100;
    private const int DefaultWarmup = 10;
    private const int DefaultBudgetMs = 300;
    private const string DefaultBaseUrl = "http://localhost:5080";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var baseUrl = (Arg(args, "--base-url") ?? DefaultBaseUrl).TrimEnd('/');
        var iterations = IntArg(args, "--n", DefaultIterations);
        var warmup = IntArg(args, "--warmup", DefaultWarmup);
        var budgetMs = IntArg(args, "--budget-ms", DefaultBudgetMs);

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            // Generous: a cold first request can JIT a whole query pipeline. Warmup absorbs it.
            Timeout = TimeSpan.FromSeconds(60),
        };

        try
        {
            await AuthenticateAsync(http, cookies, baseUrl, ct);
            var targets = await ResolveTargetsAsync(http, ct);

            Console.WriteLine(
                $"perf-probe: {baseUrl} · org=load · n={iterations} (warmup {warmup}) · budget p95 < {budgetMs} ms");
            Console.WriteLine();

            var failed = 0;
            foreach (var (label, path) in targets)
            {
                var stats = await MeasureAsync(http, label, path, iterations, warmup, ct);
                var over = stats.P95 >= budgetMs;
                failed += over ? 1 : 0;

                Console.WriteLine(
                    $"  {(over ? "FAIL" : "ok  ")}  {label,-22}  " +
                    $"p50 {stats.P50,7:F1} ms   p95 {stats.P95,7:F1} ms   p99 {stats.P99,7:F1} ms   " +
                    $"min {stats.Min,6:F1}   max {stats.Max,7:F1}");
            }

            Console.WriteLine();
            if (failed > 0)
            {
                await Console.Error.WriteLineAsync(
                    $"perf-probe: {failed} of {targets.Count} read path(s) missed the p95 < {budgetMs} ms budget. " +
                    "Profile the offender with EXPLAIN (ANALYZE, BUFFERS) before changing anything.");
                return 1;
            }

            Console.WriteLine($"perf-probe: all {targets.Count} read paths within the p95 < {budgetMs} ms budget.");
            return 0;
        }
        catch (HttpRequestException ex)
        {
            await Console.Error.WriteLineAsync(
                $"perf-probe: could not reach {baseUrl} ({ex.Message}). Start the host first, e.g. " +
                "`$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web`.");
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync($"perf-probe: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Mirrors the SPA's login sequence exactly (see <c>web/src/api/client.ts</c>): prime the
    /// antiforgery cookie, then echo it as <c>X-XSRF-TOKEN</c> on the login POST. Verifies the
    /// session with <c>/api/auth/me</c> rather than parsing the login body, so an MFA-required or
    /// otherwise non-authenticating 200 cannot be mistaken for success.
    /// </summary>
    private static async Task AuthenticateAsync(
        HttpClient http, CookieContainer cookies, string baseUrl, CancellationToken ct)
    {
        (await http.GetAsync("/api/auth/csrf", ct)).EnsureSuccessStatusCode();

        var xsrf = cookies.GetCookies(new Uri(baseUrl))["XSRF-TOKEN"]?.Value
            ?? throw new InvalidOperationException("the host did not issue an XSRF-TOKEN cookie.");

        var payload = JsonSerializer.Serialize(new { email = LoadSeeder.AdminEmail, password = LoadSeeder.AdminPassword });
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        login.Headers.Add("X-XSRF-TOKEN", xsrf);

        var response = await http.SendAsync(login, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"login as {LoadSeeder.AdminEmail} failed ({(int)response.StatusCode}). " +
                "Seed the fixture first: `dotnet run --project src/LeaseBook.Web -- seed --org load`.");
        }

        var me = await http.GetAsync("/api/auth/me", ct);
        if (!me.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "login returned success but the session is not authenticated (MFA enrolment pending?).");
        }
    }

    /// <summary>
    /// The four read paths under budget. The bank id is a <see cref="LoadSeeder"/> constant; the
    /// tenant and owner are discovered at run time because their ids are generated per seed run.
    /// Sorts to the most-active row so each read is a realistic one, not an empty projection, and
    /// targets the fixture's last activity month so the statement has real figures in it — a
    /// statement for a quiet period returns zeros and would time an unrepresentatively cheap read.
    /// </summary>
    private static async Task<IReadOnlyList<(string Label, string Path)>> ResolveTargetsAsync(
        HttpClient http, CancellationToken ct)
    {
        var tenantId = await FirstIdAsync(
            http, "/api/directory/tenants?page=1&pageSize=1&sort=balance:desc", "tenants", ct);
        var ownerId = await FirstIdAsync(
            http, "/api/directory/owners?page=1&pageSize=1", "owners", ct);

        var period = LoadSeeder.LastActivityMonth;

        return
        [
            ("tenant ledger", $"/api/accounting/tenants/{tenantId}/ledger"),
            ("dashboard", "/api/dashboard"),
            ("bank register", $"/api/accounting/banks/{LoadSeeder.OperatingTrustId}/register"),
            ("owner statement", $"/api/statements/{ownerId}?year={period.Year}&month={period.Month}&basis=cash"),
        ];
    }

    /// <summary>First id from a paged directory list, or a seed-the-fixture error if the list is empty.</summary>
    private static async Task<Guid> FirstIdAsync(HttpClient http, string path, string what, CancellationToken ct)
    {
        var response = await http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var items = doc.RootElement.GetProperty("items");
        return items.GetArrayLength() == 0
            ? throw new InvalidOperationException(
                $"the load org has no {what}. Seed it: `dotnet run --project src/LeaseBook.Web -- seed --org load`.")
            : items[0].GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Times <paramref name="iterations"/> serial requests after a warmup, reading each response body
    /// to completion so the measurement covers serialization rather than just time-to-headers. Serial
    /// by design: this measures per-request latency, not throughput under concurrency.
    /// </summary>
    private static async Task<Stats> MeasureAsync(
        HttpClient http, string label, string path, int iterations, int warmup, CancellationToken ct)
    {
        for (var i = 0; i < warmup; i++)
        {
            await SendAsync(http, label, path, ct);
        }

        var samples = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var clock = Stopwatch.StartNew();
            await SendAsync(http, label, path, ct);
            clock.Stop();
            samples[i] = clock.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return new Stats(
            P50: Percentile(samples, 0.50),
            P95: Percentile(samples, 0.95),
            P99: Percentile(samples, 0.99),
            Min: samples[0],
            Max: samples[^1]);
    }

    /// <summary>A non-2xx here would silently time an error page, so it fails the run instead.</summary>
    private static async Task SendAsync(HttpClient http, string label, string path, CancellationToken ct)
    {
        var response = await http.GetAsync(path, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"'{label}' returned {(int)response.StatusCode} for {path} — timing an error response would be meaningless.");
        }

        _ = await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Nearest-rank percentile on an already-sorted sample (no interpolation).</summary>
    private static double Percentile(double[] sorted, double q)
    {
        var rank = (int)Math.Ceiling(q * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int IntArg(string[] args, string name, int fallback) =>
        Arg(args, name) is { } raw && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private readonly record struct Stats(double P50, double P95, double P99, double Min, double Max);
}
