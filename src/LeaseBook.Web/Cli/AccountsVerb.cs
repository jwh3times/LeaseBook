using LeaseBook.Web.Auth;

namespace LeaseBook.Web.Cli;

internal sealed class AccountsVerb : ICliVerb
{
    public string Name => "accounts";

    public bool TryCreateInvocation(string[] args, out CliInvocation invocation, out string error)
    {
        invocation = null!;
        error = "Usage: accounts create-admin --org-name <name> --email <email> --name <name> (password on stdin); " +
            "accounts reset-mfa --org <uuid> --email <email> --identity-verified yes";
        if (args.Length != 8) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i += 2)
        {
            if (string.IsNullOrWhiteSpace(args[i + 1]) || !values.TryAdd(args[i], args[i + 1])) return false;
        }
        var create = args[1] == "create-admin" && values.ContainsKey("--org-name") && values.ContainsKey("--email") && values.ContainsKey("--name");
        var reset = args[1] == "reset-mfa" && values.TryGetValue("--org", out var rawOrg) && Guid.TryParse(rawOrg, out var parsedOrg)
            && parsedOrg != Guid.Empty && values.ContainsKey("--email") && values.GetValueOrDefault("--identity-verified") == "yes";
        if (!create && !reset) return false;
        invocation = new CliInvocation(Name, async (services, ct) =>
        {
            try
            {
                await RoleSeeder.EnsureRolesAsync(services, ct);
                await using var scope = services.CreateAsyncScope();
                var accounts = scope.ServiceProvider.GetRequiredService<AccountAdministration>();
                if (create)
                {
                    if (!Console.IsInputRedirected)
                        throw new InvalidOperationException("Supply the initial password through standard input from a secure source; never as a command argument.");
                    var password = await Console.In.ReadLineAsync(ct)
                        ?? throw new InvalidOperationException("An initial password is required on standard input.");
                    var id = await accounts.CreateAdminAsync(values["--org-name"], values["--email"], values["--name"], password, ct);
                    Console.WriteLine($"Organization {id} created. The administrator must enroll MFA at first sign-in.");
                }
                else
                {
                    await accounts.ResetMfaAsync(Guid.Parse(values["--org"]), values["--email"], ct);
                    Console.WriteLine("MFA reset completed. Existing sessions and recovery codes are invalid; enroll again at sign-in.");
                }
                return CliExitCodes.Success;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Identity/database exceptions can contain submitted account data. The CLI emits no payloads.
                Console.Error.WriteLine("Account operation failed. Check the arguments, password policy, database access, and existing accounts.");
                return CliExitCodes.Failure;
            }
        });
        return true;
    }
}
