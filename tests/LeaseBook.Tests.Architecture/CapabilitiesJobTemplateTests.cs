using System.Text.RegularExpressions;
using LeaseBook.Web.Capabilities;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// Pins the two hand-written Container Apps Job invocations against the Bicep that declares the jobs
/// (ADR-027, ADR-028): <c>infra/jobs/capabilities-exec.yaml</c> and the migrator start in
/// <c>.github/workflows/deploy-prod.yml</c>.
/// <para>
/// <b>Why a test and not a comment.</b> <c>az containerapp job start</c> does not merge with a job's
/// deployed template — it sends the execution template it is given — so every field an invocation
/// omits or misspells is simply absent from that execution. Nothing rejects it: the YAML path
/// deserializes through a matcher that is case-sensitive and DISCARDS unrecognised keys silently, and
/// the flag path builds a container out of exactly the flags passed. Both failure modes surface as a
/// container that starts and then dies on missing configuration, which is indistinguishable from the
/// Key Vault secret not having been wired yet. This is drift that cannot be caught downstream, so it
/// is caught here, against the same source of truth the deployment uses.
/// </para>
/// <para>
/// Same reasoning as <see cref="CapabilityAgeTests"/> pinning
/// <c>CapabilityAge.RegistryRelativePath</c>: a string that two files must agree on, where
/// disagreement is silent.
/// </para>
/// </summary>
public sealed class CapabilitiesJobTemplateTests
{
    // The container, secret and env names as declared in modules/containerapp.bicep. Every assertion
    // below checks BOTH files against these, so renaming one side fails rather than drifting.
    private const string CapabilitiesContainer = "capabilities";
    private const string CapabilitiesSecret = "connectionstrings-default";
    private const string CapabilitiesConnectionEnv = "ConnectionStrings__Default";
    private const string MigratorContainer = "migrate";
    private const string MigratorSecret = "connectionstrings-migrations";
    private const string MigratorConnectionEnv = "ConnectionStrings__Migrations";

    private const string ExecTemplatePath = "infra/jobs/capabilities-exec.yaml";
    private const string BicepPath = "infra/modules/containerapp.bicep";
    private const string DeployProdPath = ".github/workflows/deploy-prod.yml";

    /// <summary>
    /// The execution template names the same container the Bicep declares. An execution whose
    /// container name does not match is not a no-op: it also breaks
    /// <c>az containerapp job logs show --container</c>, which matches the EXECUTION's container name
    /// — so the invocation and the command used to diagnose it fail together.
    /// </summary>
    [Fact]
    public void The_execution_template_matches_the_container_the_bicep_declares()
    {
        var yaml = ReadRepoFile(ExecTemplatePath);
        var bicep = ReadRepoFile(BicepPath);

        Container(yaml).ShouldBe(
            CapabilitiesContainer,
            $"{ExecTemplatePath} must name the container declared in {BicepPath}");

        bicep.ShouldContain(
            $"name: '{CapabilitiesContainer}'",
            Case.Sensitive,
            $"{BicepPath} declares the capabilities container; renaming it means updating " +
            $"{ExecTemplatePath} and the runbook's `job logs show --container` in the same change");

        bicep.ShouldContain(
            $"name: '{CapabilitiesSecret}'",
            Case.Sensitive,
            $"the secret {ExecTemplatePath} references by secretRef must exist on the job");
    }

    /// <summary>
    /// Every environment variable the deployed template supplies is present in the execution template,
    /// plus <c>LEASEBOOK_OPERATOR</c>. Anything omitted is absent from the execution, and the two that
    /// matter fail in ways that read like something else entirely: no connection string looks like an
    /// unwired Key Vault, and no log-level override buries the verb's own error under an EF stack.
    /// </summary>
    [Fact]
    public void The_execution_template_supplies_every_variable_the_job_declares()
    {
        var yaml = ReadRepoFile(ExecTemplatePath);
        var bicep = ReadRepoFile(BicepPath);

        var declared = Regex.Matches(bicep, @"name: '(Logging__LogLevel__[^']+)'")
            .Select(m => m.Groups[1].Value)
            .ToList();

        declared.Count.ShouldBe(
            2,
            $"{BicepPath} is expected to quiet exactly the two EF categories; if that changed, " +
            $"{ExecTemplatePath} has to change with it");

        var supplied = EnvNames(yaml);

        foreach (var name in declared.Append(CapabilitiesConnectionEnv))
        {
            supplied.ShouldContain(
                name,
                $"{ExecTemplatePath} must supply '{name}': a per-execution template REPLACES the " +
                "job's container spec rather than extending it, so an omitted variable is simply " +
                "not there");
        }

        supplied.ShouldContain(
            CapabilitiesCommand.OperatorVariable,
            $"{ExecTemplatePath} must carry {CapabilitiesCommand.OperatorVariable}, which the " +
            "deployed template deliberately does not default");
    }

    /// <summary>
    /// The secret-backed entry uses <c>secretRef</c> in that exact casing, which is the only spelling
    /// the CLI's deserializer binds.
    /// <para>
    /// <b>This is the assertion the env-name check above cannot make.</b> A typo does not remove the
    /// variable — it keeps <c>name:</c> and drops the reference, leaving a valued-less env var. The
    /// container then dies on a missing connection string, which is exactly how it dies when
    /// <c>defaultSecretUri</c> has not been supplied yet — a state <c>infra/README.md</c> documents as
    /// normal before the role bootstrap. So the plausible diagnosis is the wrong one, and the operator
    /// goes to Key Vault RBAC. Every near-miss below was confirmed against the real deserializer to be
    /// discarded in silence: <c>secretref</c>, <c>secret_ref</c>, <c>SecretRef</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void The_secret_reference_uses_the_only_key_the_deserializer_binds()
    {
        var yaml = ReadRepoFile(ExecTemplatePath);

        Regex.IsMatch(
                yaml,
                $@"^      - name: {Regex.Escape(CapabilitiesConnectionEnv)}\r?\n        secretRef: {Regex.Escape(CapabilitiesSecret)}$",
                RegexOptions.Multiline)
            .ShouldBeTrue(
                $"{ExecTemplatePath} must reference the secret as exactly `secretRef: " +
                $"{CapabilitiesSecret}` on the line after `- name: {CapabilitiesConnectionEnv}`. The " +
                "deserializer matches case-sensitively and discards what it does not recognise " +
                "without an error, so `secretref`/`secret_ref`/`SecretRef` all yield an env var with " +
                "no value — and that is indistinguishable from an unwired Key Vault secret.");

        foreach (var nearMiss in (string[])["secretref:", "secret_ref:", "SecretRef:", "Name:", "Value:"])
        {
            Significant(yaml).ShouldNotContain(
                nearMiss,
                Case.Sensitive,
                $"`{nearMiss}` is silently discarded by the execution-template deserializer");
        }
    }

    /// <summary>
    /// Every structural key is present in the exact casing the deserializer binds.
    /// <para>
    /// <b><c>Env:</c> is the reason this test exists separately from the env-name check.</b> That check
    /// reads indentation, not structure, so it still finds the variables underneath a mis-cased
    /// <c>Env:</c> — while the deserializer drops the entire array and sends a container with no
    /// environment at all. Same misdiagnosis as a dropped <c>secretRef</c>, and wider.
    /// </para>
    /// <para>
    /// <c>Containers:</c> is worse and is why the top level is asserted too: the CLI emits an empty
    /// <c>{}</c> envelope, and what the resource provider does with that is unverified. If it starts
    /// the job on its deployed default template, a <c>flag disable</c> becomes a read-only listing that
    /// exits 0 — a mutation that silently did not happen.
    /// </para>
    /// <para>
    /// Verified case by case against the CLI's own deserializer: every one of these is discarded in
    /// silence, and none of them errors.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("^containers:$", "Containers")]
    [InlineData(@"^  - name: \S+$", "the container name")]
    [InlineData("^    image: .+$", "the image")]
    [InlineData("^    args:$", "args")]
    [InlineData("^    env:$", "env")]
    [InlineData("^    resources:$", "resources")]
    public void Every_structural_key_is_spelled_the_only_way_that_binds(string pattern, string what)
    {
        Regex.IsMatch(Significant(ReadRepoFile(ExecTemplatePath)), pattern, RegexOptions.Multiline)
            .ShouldBeTrue(
                $"{ExecTemplatePath} must declare {what} matching /{pattern}/. The execution-template " +
                "deserializer matches keys case-sensitively and DISCARDS what it does not recognise " +
                "without an error, so a capitalised key here removes that whole section from the " +
                "execution rather than failing the command.");
    }

    /// <summary>
    /// The operator variable ships EMPTY, so a forgotten edit is refused by
    /// <see cref="CapabilitiesCommand.AttributionRefusal"/> rather than recorded. A placeholder like
    /// <c>your-name-here</c> would be worse than nothing: <c>platform_audit_events</c> is append-only,
    /// so it could never be corrected. This asserts the fail-closed direction through the real guard.
    /// </summary>
    [Fact]
    public void A_forgotten_operator_edit_is_refused_rather_than_recorded()
    {
        // The match is asserted BEFORE its value is used. A regex that stopped matching would return
        // "" — which refuses, so this test would keep passing while asserting nothing about the file.
        // That is the exact shape of a gate that is believed rather than armed.
        var entry = EnvValueMatch(ReadRepoFile(ExecTemplatePath), CapabilitiesCommand.OperatorVariable);

        entry.Success.ShouldBeTrue(
            $"{ExecTemplatePath} must declare `- name: {CapabilitiesCommand.OperatorVariable}` " +
            "followed by a `value:` line; this test cannot say anything about a file it failed to read");

        var shipped = entry.Groups[1].Value;

        CapabilitiesCommand.AttributionRefusal(
                CapabilitiesActionKind.FlagDisable, isDevelopment: false, configuredOperator: shipped)
            .ShouldNotBeNull(
                $"{ExecTemplatePath} must ship {CapabilitiesCommand.OperatorVariable} blank so an " +
                "unedited copy is refused. A plausible placeholder would land in an append-only " +
                "audit trail and could never be corrected.");
    }

    /// <summary>
    /// <b>The assertion that would have caught the shipped-and-broken invocation.</b> The template's
    /// <c>args</c> are run through the real parser, so the file cannot ship something the verb would
    /// reject — most importantly the one-argv-element form, where a whole command line is quoted into
    /// a single entry. That is exactly what the `--args` flag produces when a dash-prefixed token
    /// forces it to be quoted, and the container's response is to boot the ASP.NET host instead of
    /// running anything.
    /// </summary>
    [Fact]
    public void The_templates_args_are_a_command_the_verb_actually_accepts()
    {
        var args = Args(ReadRepoFile(ExecTemplatePath));

        args.Count.ShouldBeGreaterThan(
            1, "one entry means a whole command line was collapsed into a single argv element");

        CapabilitiesVerb.TryResolve([.. args], out _, out var error)
            .ShouldBeTrue($"{ExecTemplatePath} must ship a runnable invocation, but: {error}");

        // Negative control, so the reason this file is shaped one-token-per-entry is pinned rather
        // than remembered: the collapsed form parses as YAML and is rejected by the verb.
        CapabilitiesVerb.TryResolve([string.Join(' ', args)], out _, out _)
            .ShouldBeFalse("a collapsed command line must not be mistaken for a valid invocation");
    }

    /// <summary>
    /// <c>deploy-prod.yml</c> starts the migrator job with the flag form (it needs no dash-prefixed
    /// arguments), which means it must name the container, the secret-backed variable and the log
    /// container itself. Every one of those is a literal that has to agree with the Bicep, and the
    /// consequence of disagreement is severe and silent: an execution with no connection string, and
    /// a log dump that returns nothing because it is asking for the wrong container.
    /// </summary>
    [Fact]
    public void The_prod_migration_workflow_matches_the_migrator_job_the_bicep_declares()
    {
        var bicep = ReadRepoFile(BicepPath);
        var workflow = ReadRepoFile(DeployProdPath);

        bicep.ShouldContain($"name: '{MigratorContainer}'", Case.Sensitive);
        bicep.ShouldContain($"name: '{MigratorSecret}'", Case.Sensitive);
        bicep.ShouldContain($"name: '{MigratorConnectionEnv}'", Case.Sensitive);

        workflow.ShouldContain(
            $"--container-name {MigratorContainer}",
            Case.Sensitive,
            "without it the CLI names the execution's container after the JOB, and the execution " +
            "carries none of the template's configuration");

        workflow.ShouldContain(
            $"--container {MigratorContainer}",
            Case.Sensitive,
            "`job logs show --container` matches the EXECUTION's container name, so the failure-path " +
            "log dump returns nothing when these disagree");

        workflow.ShouldContain(
            $"{MigratorConnectionEnv}=secretref:{MigratorSecret}",
            Case.Sensitive,
            "a per-execution override replaces the container's env, so the migrator connection " +
            "string has to be restated or the migration runs with none");
    }

    // ── Minimal readers ─────────────────────────────────────────────────────────────────────────
    //
    // Deliberately literal rather than a YAML library. The failure being guarded against IS key
    // spelling, so a tolerant parser that accepted `secretref` would defeat the test it is serving.

    private static string Container(string yaml) =>
        Regex.Match(yaml, @"^  - name: (\S+)$", RegexOptions.Multiline).Groups[1].Value;

    private static List<string> EnvNames(string yaml) =>
        [.. Regex.Matches(yaml, @"^      - name: (\S+)$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)];

    /// <summary>
    /// The <c>value:</c> that follows a given <c>- name:</c>. Matches <c>value</c> only in that exact
    /// casing, because that is the only casing the CLI's deserializer binds. Returns the
    /// <see cref="Match"/> rather than the string so callers must check <c>Success</c> — a miss and a
    /// deliberately blank value are both the empty string, and only one of them is the file being right.
    /// </summary>
    private static Match EnvValueMatch(string yaml, string name) =>
        Regex.Match(yaml, $@"^      - name: {Regex.Escape(name)}\r?\n        value: ""?([^""\r\n]*)""?$",
            RegexOptions.Multiline);

    /// <summary>
    /// The file with comment lines removed. The template documents its own near-miss spellings by name,
    /// and a check that tripped over that explanation would be its own false positive.
    /// </summary>
    private static string Significant(string yaml) =>
        string.Join('\n', yaml.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    private static List<string> Args(string yaml)
    {
        var block = Regex.Match(yaml, @"^    args:\r?\n((?:      - .+\r?\n)+)", RegexOptions.Multiline);
        block.Success.ShouldBeTrue($"{ExecTemplatePath} must declare an `args:` list");

        return [.. Regex.Matches(block.Groups[1].Value, @"^      - (.+?)\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)];
    }

    private static string ReadRepoFile(string relativePath)
    {
        var root = CapabilityAge.FindRepoRoot();
        root.ShouldNotBeNull("these files are pinned relative to the repository root");

        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).ShouldBeTrue($"{relativePath} must exist; the deployment procedure cites it");

        return File.ReadAllText(full);
    }
}
