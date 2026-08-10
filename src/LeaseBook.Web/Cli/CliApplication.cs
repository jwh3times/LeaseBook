using LeaseBook.Web.Capabilities;
using LeaseBook.Web.Seeding;

namespace LeaseBook.Web.Cli;

/// <summary>
/// The foreground operator-tool interface for this executable. The registry is the single source of
/// truth for both identifying a CLI process (and therefore suppressing noisy EF logs) and dispatching
/// it, so adding a verb cannot update one concern while forgetting the other.
/// </summary>
internal static class CliApplication
{
    private static readonly IReadOnlyList<ICliVerb> Verbs =
    [
        new SeedVerb(),
        new InvariantSweepVerb(),
        new CapabilitiesCliVerb(),
        new PerfProbeVerb(),
    ];

    /// <summary>Resolves one invocation without touching configuration, DI, or the database.</summary>
    public static CliResolution Resolve(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return CliResolution.NotCli;
        }

        var verb = Verbs.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, args[0], StringComparison.Ordinal));
        if (verb is null)
        {
            return CliResolution.NotCli;
        }

        return verb.TryCreateInvocation(args, out var invocation, out var error)
            ? CliResolution.Success(invocation)
            : CliResolution.Failure(error);
    }
}

/// <summary>One registered CLI grammar and its typed executable invocation.</summary>
internal interface ICliVerb
{
    string Name { get; }

    bool TryCreateInvocation(string[] args, out CliInvocation invocation, out string error);
}

/// <summary>The process-wide exit-code vocabulary shared by every registered verb.</summary>
internal static class CliExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Unavailable = 2;
}

/// <summary>A successfully parsed foreground command, ready to execute after the host is composed.</summary>
internal sealed class CliInvocation(
    string verb,
    Func<IServiceProvider, CancellationToken, Task<int>> runAsync)
{
    public string Verb { get; } = verb;

    public Task<int> RunAsync(IServiceProvider services, CancellationToken ct) => runAsync(services, ct);
}

/// <summary>
/// The three possible results of looking at argv: not a LeaseBook CLI process, a valid invocation,
/// or a recognized verb whose arguments are invalid.
/// </summary>
internal sealed record CliResolution(bool IsCli, CliInvocation? Invocation, string? Error)
{
    public static CliResolution NotCli { get; } = new(false, null, null);

    public static CliResolution Success(CliInvocation invocation) => new(true, invocation, null);

    public static CliResolution Failure(string error) => new(true, null, error);
}
