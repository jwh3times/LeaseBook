using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-02 smoke test: proves the shared <see cref="PostgresFixture"/> boots, migrations apply, and the
/// <see cref="OrgScope"/> harness entry point works from this assembly — the foundation every WP-03+
/// accounting test stands on. Asserts only fixture mechanics (an org round-trips), not accounting
/// behaviour.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class FixtureSmokeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task OrgScope_creates_a_fresh_org_visible_under_its_own_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await OrgScope.CreateAsync(fixture, ct);

        long count = 0;
        await scope.RunAsync(async () =>
        {
            count = await scope.Db.Orgs.CountAsync(o => o.Id == scope.OrgId, ct);
        }, ct);

        count.ShouldBe(1);
    }
}
