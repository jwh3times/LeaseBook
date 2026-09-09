using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

public sealed class DbAdminJobTests
{
    private static string Read(string path) => RepositorySource.Current.File(path).SignificantText;

    [Fact]
    public void Administration_is_manual_and_isolated_from_the_app_identity()
    {
        var module = Read("infra/modules/dbadmin.bicep");
        module.ShouldContain("triggerType: 'Manual'");
        module.ShouldContain("replicaRetryLimit: 0");
        module.ShouldContain("parallelism: 1");
        module.ShouldContain("args: ['refuse']");
        module.ShouldContain("name: '${prefix}-dbadmin-id'");
        module.ShouldContain("name: '${prefix}-dbadmin-kv'");
        module.ShouldContain("scope: vault");
        module.ShouldContain("principalId: identity.properties.principalId");
        module.ShouldContain("dependsOn: [acrPull, secretsUser]");
        module.ShouldNotContain("scheduleTriggerConfig");
        module.ShouldNotContain("eventTriggerConfig");
        var main = Read("infra/main.bicep");
        main.ShouldContain("if (env == 'prod' && enablePrivateNetworking)");
        main.ShouldContain("environmentId: app.outputs.environmentId");
        Read("infra/modules/containerapp.bicep").ShouldNotContain("dbadmin-kv");
    }

    [Theory]
    [InlineData("bootstrap", 4)]
    [InlineData("verify", 1)]
    public void Execution_templates_preserve_resources_and_only_supply_secret_references(string operation, int secretCount)
    {
        var template = Read($"infra/jobs/dbadmin-{operation}-exec.yaml");
        template.ShouldContain("containers:\n  - name: dbadmin");
        template.ShouldContain($"args: [{operation}]");
        template.ShouldContain("cpu: 0.5");
        template.ShouldContain("memory: 1Gi");
        foreach (var name in new[] { "PGHOST", "LEASEBOOK_CONFIRM_HOST", "LEASEBOOK_OPERATOR" })
            template.ShouldContain($"- name: {name}\n        value: \"\"");

        var secrets = Regex.Matches(template, @"secretRef: ([a-z-]+)");
        secrets.Count.ShouldBe(secretCount);
        foreach (Match secret in secrets)
            Read("infra/modules/dbadmin.bicep").ShouldContain($"name: '{secret.Groups[1].Value}'");

        if (operation == "verify")
        {
            template.ShouldNotContain("postgres-admin-password");
            template.ShouldNotContain("postgres-app-password");
            template.ShouldNotContain("postgres-migrator-password");
        }
    }
}
