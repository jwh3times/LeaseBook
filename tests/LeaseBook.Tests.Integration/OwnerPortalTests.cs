using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Tests.Integration.Observability;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Portal;
using LeaseBook.Web.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The ADR-003 portal-persona pack for the owner portal (#464): an owner reads only their own money,
/// resolved from a host-owned link on every request, through an explicit allow-list projection.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed partial class OwnerPortalTests(PostgresFixture fixture)
{
    private const string SummaryPath = "/api/portal/owner/summary";
    private const string StatementsPath = "/api/portal/owner/statements";
    private const string ProcessName = "test:owner-portal";

    private static string PdfPath(Guid artifactId) => $"{StatementsPath}/{artifactId}/pdf";

    // Free text the fixture writes into journal descriptions, void reasons and Directory rows. None of
    // it is owner-portal copy, so none of it may appear in any owner-portal JSON response.
    // The PDF endpoint is deliberately exempt: it serves the issued statement byte-for-byte, the
    // document the manager already issued to this owner, and issued statements carry line
    // descriptions (ADR-003). Scanning its compressed streams for these strings would prove nothing;
    // the byte-identity test is what pins that endpoint.
    private static readonly string[] SeededSecrets =
    [
        "Internal", "memo", "reason", "reference",
        "Fixture trust", "Fixture bank", "Fixture deposit trust", PortalSeeder.DepositBankMask,
        "Resident A", "Resident B", "Resident C", "Deposit resident",
        "1234.56", // the deposit liability held for Owner A's tenant
    ];

    [Fact]
    public async Task Fixture_owners_see_only_their_own_money_through_an_allowlisted_projection()
    {
        var ct = TestContext.Current.CancellationToken;
        await PortalSeeder.SeedAsync(fixture.Api.Services, ct);
        await PortalSeeder.SeedAsync(fixture.Api.Services, ct); // idempotent

        // Every fixture owner and issued artifact across both orgs: the forged-selector corpus.
        var allOwners = new List<Guid>();
        var allArtifacts = new List<(Guid OrgId, Guid OwnerId, Guid Id, string Key)>();
        foreach (var orgId in new[] { PortalSeeder.PortalOrgId, PortalSeeder.OtherOrgId })
        {
            await InOrg(orgId, async sp =>
            {
                var db = sp.GetRequiredService<AppDbContext>();
                allOwners.AddRange(await db.Set<Owner>().Where(o => !o.IsSystem).Select(o => o.Id).ToListAsync(ct));
                allArtifacts.AddRange((await db.Set<StatementArtifact>().ToListAsync(ct))
                    .Select(a => (orgId, a.OwnerId, a.Id, a.ArtifactKey)));
                // The fixture posts real events (deposit, fee, disbursements, a void): its books must hold.
                (await sp.GetRequiredService<IInvariantChecks>().CheckCoreAsync(ct)).ShouldBeEmpty();
            }, ct);
        }

        var sawDepositExcluded = false;
        foreach (var (email, name, orgId) in new[]
        {
            (PortalSeeder.OwnerAEmail, "Owner A", PortalSeeder.PortalOrgId),
            (PortalSeeder.OwnerBEmail, "Owner B", PortalSeeder.PortalOrgId),
            (PortalSeeder.OwnerCEmail, "Owner C", PortalSeeder.OtherOrgId),
        })
        {
            // The real Accounting reads, on the org's own basis, for the owner this login is linked to.
            var ownerId = Guid.Empty;
            string basis = "";
            OwnerLedgerResponse? ledger = null;
            OwnerBalanceRow? balances = null;
            await InOrg(orgId, async sp =>
            {
                var db = sp.GetRequiredService<AppDbContext>();
                var sender = sp.GetRequiredService<ISender>();
                var userId = await db.Users.Where(u => u.OrgId == orgId && u.Email == email).Select(u => u.Id).SingleAsync(ct);
                ownerId = (await db.Set<OwnerAccess>().SingleAsync(l => l.UserId == userId && l.RevokedAt == null, ct)).OwnerId;
                basis = (await sender.Query(new GetOrgSettings(), ct)).AccountingBasis;
                ledger = await sender.Query(new GetOwnerLedger(ownerId, basis), ct);
                balances = (await sender.Query(new GetOwnerBalances(basis), ct)).Rows.Single(r => r.OwnerId == ownerId);
            }, ct);
            var own = allArtifacts.Where(a => a.OrgId == orgId && a.OwnerId == ownerId).ToList();
            own.ShouldNotBeEmpty($"{name} must have at least one issued statement in the fixture");

            using var client = await Login(email, PortalSeeder.Password, ct);
            // Selectors in query strings and headers must not influence scope.
            client.DefaultRequestHeaders.Add("X-Org-Id", PortalSeeder.OtherOrgId.ToString());
            var forgedQuery = $"?ownerId={allOwners.First(o => o != ownerId)}&orgId={PortalSeeder.OtherOrgId}&propertyId={UuidV7.NewId()}";

            // ── Summary ────────────────────────────────────────────────────────────
            var summaryResponse = await client.GetAsync(SummaryPath + forgedQuery, ct);
            summaryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            summaryResponse.Headers.CacheControl!.NoStore.ShouldBeTrue();
            var summaryJson = await summaryResponse.Content.ReadAsStringAsync(ct);
            var summary = JsonSerializer.Deserialize<OwnerPortalSummary>(summaryJson, JsonSerializerOptions.Web)!;
            summary.OwnerName.ShouldBe(name);
            summary.Basis.ShouldBe(basis);
            summary.Balance.ShouldBe(ledger!.Balance);
            summary.Balance.ShouldBe(balances!.Operating, "held in trust is owner equity on the org basis");
            if (balances.Deposits != 0m)
            {
                summary.Balance.ShouldNotBe(balances.Total, "deposits are tenant liabilities, not owner money");
                sawDepositExcluded = true;
            }

            summary.Activity.Count.ShouldBe(ledger.Rows.Count);
            summary.Activity.Select(r => r.Balance).ShouldBe(ledger.Rows.Select(r => r.Balance));
            summary.Activity.Select(r => r.Amount).ShouldBe(ledger.Rows.Select(r => r.Amount));
            summary.Activity.Select(r => r.IsVoided).ShouldBe(ledger.Rows.Select(r => r.IsVoided));
            summary.Activity.Select(r => r.IsReversal).ShouldBe(ledger.Rows.Select(r => r.ReversesEntryId is not null));
            summary.Activity.ShouldAllBe(r => AllowedCategories.Contains(r.Category));

            var disbursed = ledger.Rows.Where(r => r.EventType == "OwnerDisbursed").ToList();
            disbursed.ShouldNotBeEmpty();
            summary.Disbursements.Count(d => !d.IsReversal).ShouldBe(disbursed.Count);
            summary.Disbursements.Where(d => !d.IsReversal).Select(d => d.Amount).ShouldBe(disbursed.Select(r => -r.Amount));
            summary.Disbursements.Where(d => !d.IsReversal).Select(d => d.IsVoided).ShouldBe(disbursed.Select(r => r.IsVoided));

            // Forging the owner id changes nothing.
            foreach (var forgedOwner in allOwners)
            {
                var forged = await client.GetFromJsonAsync<OwnerPortalSummary>(SummaryPath + $"?ownerId={forgedOwner}", ct);
                forged!.OwnerName.ShouldBe(name);
                forged.Balance.ShouldBe(summary.Balance);
            }

            // ── Statements ─────────────────────────────────────────────────────────
            var statementsResponse = await client.GetAsync(StatementsPath + forgedQuery, ct);
            statementsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            statementsResponse.Headers.CacheControl!.NoStore.ShouldBeTrue();
            var statementsJson = await statementsResponse.Content.ReadAsStringAsync(ct);
            var statements = JsonSerializer.Deserialize<OwnerPortalStatements>(statementsJson, JsonSerializerOptions.Web)!;
            statements.Statements.Select(s => s.Id).Order().ShouldBe(own.Select(a => a.Id).Order());
            statements.Statements.Select(s => s.PeriodYear * 100 + s.PeriodMonth)
                .ShouldBe(statements.Statements.Select(s => s.PeriodYear * 100 + s.PeriodMonth).OrderDescending());
            statements.Statements.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.Scope));

            // ── PDFs: own bytes verbatim; everything else one indistinguishable not-found ──────
            using (var scope = fixture.Api.Services.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
                foreach (var artifact in own)
                {
                    var pdf = await client.GetAsync(PdfPath(artifact.Id) + forgedQuery, ct);
                    pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
                    pdf.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
                    pdf.Headers.CacheControl!.NoStore.ShouldBeTrue();
                    (await pdf.Content.ReadAsByteArrayAsync(ct)).ShouldBe((await store.GetAsync(artifact.Key, ct))!);
                }
            }

            var nonexistent = await NotFoundShape(client, UuidV7.NewId(), ct);
            foreach (var foreign in allArtifacts.Where(a => !(a.OrgId == orgId && a.OwnerId == ownerId)))
            {
                (await NotFoundShape(client, foreign.Id, ct)).ShouldBe(nonexistent);
            }

            // ── Allow-list: the exact property names, and no seeded free text anywhere ──────────
            AssertProperties(summaryJson, "activity", "balance", "basis", "disbursements", "ownerName");
            using (var doc = JsonDocument.Parse(summaryJson))
            {
                foreach (var row in doc.RootElement.GetProperty("activity").EnumerateArray())
                {
                    Names(row).ShouldBe(["amount", "balance", "category", "date", "isReversal", "isVoided", "propertyAddress"]);
                }

                foreach (var row in doc.RootElement.GetProperty("disbursements").EnumerateArray())
                {
                    Names(row).ShouldBe(["amount", "date", "isReversal", "isVoided"]);
                }
            }

            AssertProperties(statementsJson, "statements");
            using (var doc = JsonDocument.Parse(statementsJson))
            {
                foreach (var row in doc.RootElement.GetProperty("statements").EnumerateArray())
                {
                    Names(row).ShouldBe(["basis", "endingBalance", "id", "issuedAt", "periodMonth", "periodYear", "scope"]);
                }
            }

            foreach (var json in new[] { summaryJson, statementsJson })
            {
                var text = GuidPattern().Replace(json, "<id>");
                foreach (var secret in SeededSecrets)
                {
                    text.ShouldNotContain(secret, Case.Insensitive, $"{name}'s owner-portal response leaked \"{secret}\"");
                }
            }

            // ── Persona: no staff surface, no tenant portal ─────────────────────────
            foreach (var path in new[]
            {
                "/api/directory/owners", "/api/accounting/owners/" + ownerId + "/ledger", "/api/dashboard",
                "/api/portal/tenant/ledger", "/api/portal/tenant/payments",
            })
            {
                (await client.GetAsync(path, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
            }
        }

        sawDepositExcluded.ShouldBeTrue("the fixture must hold a deposit for an owner so the exclusion is exercised");
    }

    [Theory]
    [InlineData(Roles.PMAdmin)]
    [InlineData(Roles.PMStaff)]
    [InlineData(Roles.Tenant)]
    public async Task Other_personas_cannot_use_the_owner_portal_even_with_a_valid_link(string role)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await CreateLinkedUser(role, ct);
        using var client = await Login(setup.Email, AuthTestSupport.DefaultPassword, ct);
        foreach (var path in new[] { SummaryPath, StatementsPath, PdfPath(setup.FirstArtifact) })
        {
            (await client.GetAsync(path, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    // Discriminating by construction: the user holds Owner *and* a valid owner link, so only the
    // exclusion clause for the added role can refuse it. A single-role user would be refused by
    // RequireRole(Owner) alone and could not tell whether the exclusion exists.
    [Theory]
    [InlineData(Roles.PMAdmin)]
    [InlineData(Roles.PMStaff)]
    [InlineData(Roles.Tenant)]
    public async Task An_owner_who_also_holds_another_role_is_refused_by_the_owner_portal(string extraRole)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await CreateLinkedUser(Roles.Owner, ct);
        await AddRole(setup.UserId, extraRole);

        using var client = await Login(setup.Email, AuthTestSupport.DefaultPassword, ct);
        foreach (var path in new[] { SummaryPath, StatementsPath, PdfPath(setup.FirstArtifact) })
        {
            (await client.GetAsync(path, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    // The mirror case for the tenant portal: Tenant role *and* a valid resident link, so only the
    // tenant persona's Owner exclusion can refuse it.
    [Fact]
    public async Task A_tenant_who_also_holds_Owner_is_refused_by_the_tenant_portal_despite_a_resident_link()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await CreateLinkedUser(Roles.Owner, ct);
        await AddRole(setup.UserId, Roles.Tenant);
        await InOrg(setup.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var tenant = new Tenant { Id = UuidV7.NewId(), DisplayName = "Dual-persona resident" };
            db.Add(tenant);
            await db.SaveChangesAsync(ct);
            await sp.GetRequiredService<ResidentAccessService>().GrantAsync(setup.UserId, tenant.Id, ct);
        }, ct);

        using var client = await Login(setup.Email, AuthTestSupport.DefaultPassword, ct);
        (await client.GetAsync("/api/portal/tenant/ledger", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task AddRole(Guid userId, string role)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId.ToString());
        AccountSecurityAudit.RequireSuccess(await users.AddToRoleAsync(user!, role));
    }

    [Fact]
    public async Task Missing_revoked_replaced_and_system_links_are_resolved_on_each_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await CreateLinkedUser(Roles.Owner, ct);
        using var client = await Login(setup.Email, AuthTestSupport.DefaultPassword, ct);
        (await ReadSummary(client, ct)).OwnerName.ShouldBe("First owner");
        (await client.GetAsync(PdfPath(setup.FirstArtifact), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await InOrg(setup.OrgId, sp => sp.GetRequiredService<OwnerAccessService>().RevokeAsync(setup.UserId, ct), ct);
        foreach (var path in new[] { SummaryPath, StatementsPath, PdfPath(setup.FirstArtifact) })
        {
            (await client.GetAsync(path, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }

        // Replaced after login: the same cookie now reads the second owner, and the first owner's
        // document is a plain not-found.
        await InOrg(setup.OrgId, sp => sp.GetRequiredService<OwnerAccessService>().GrantAsync(setup.UserId, setup.SecondOwner, ct), ct);
        (await ReadSummary(client, ct)).OwnerName.ShouldBe("Second owner");
        (await client.GetAsync(PdfPath(setup.FirstArtifact), ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<OwnerPortalStatements>(StatementsPath, ct))!.Statements.ShouldBeEmpty();

        await InOrg(setup.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var owner = await db.Set<Owner>().SingleAsync(o => o.Id == setup.SecondOwner, ct);
            owner.IsSystem = true;
            await db.SaveChangesAsync(ct);
        }, ct);
        (await client.GetAsync(SummaryPath, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await InOrg(setup.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var audits = await db.AuditEvents.Where(a => a.EntityType == "owner_access").ToListAsync(ct);
            audits.Count.ShouldBe(3); // grant, revoke, grant
            audits.ShouldAllBe(a => a.ActorKind == "system" && a.ActorProcess == ProcessName);
            audits.ShouldAllBe(a => a.After == null || !a.After.Contains("Password"));
        }, ct);

        var unlinked = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.Owner, ct);
        using var noLink = unlinked.Client;
        (await noLink.GetAsync(SummaryPath, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var anonymous = fixture.Api.CreateClient();
        (await anonymous.GetAsync(SummaryPath, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Provision_and_resolution_explicitly_reject_foreign_identity_and_system_owners()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await CreateLinkedUser(Roles.Owner, ct);
        var b = await CreateLinkedUser(Roles.Owner, ct);
        await InOrg(a.OrgId, async sp =>
        {
            var access = sp.GetRequiredService<OwnerAccessService>();
            await Should.ThrowAsync<InvalidOperationException>(() => access.GrantAsync(b.UserId, a.SecondOwner, ct));
            await Should.ThrowAsync<InvalidOperationException>(() => access.GrantAsync(a.UserId, b.SecondOwner, ct));
            await Should.ThrowAsync<InvalidOperationException>(() => access.RevokeAsync(b.UserId, ct));
            // Identity rows have no RLS: this principal names B while the ambient transaction is A.
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, b.UserId.ToString())], "test"));
            (await access.ResolveAsync(principal, ct)).ShouldBeNull();
            var db = sp.GetRequiredService<AppDbContext>();
            var system = new Owner { Id = UuidV7.NewId(), Name = "All other owners", IsSystem = true };
            db.Add(system);
            await db.SaveChangesAsync(ct);
            await Should.ThrowAsync<InvalidOperationException>(() => access.GrantAsync(a.UserId, system.Id, ct));
            // One active link per user: replacing requires an explicit revoke first.
            await Should.ThrowAsync<InvalidOperationException>(() => access.GrantAsync(a.UserId, a.SecondOwner, ct));
        }, ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Database_rejects_cross_org_references_even_when_bypassing_the_provision_helper(bool foreignUser)
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await CreateLinkedUser(Roles.Owner, ct);
        var b = await CreateLinkedUser(Roles.Owner, ct);
        var error = await Should.ThrowAsync<DbUpdateException>(() => InOrg(a.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(new OwnerAccess
            {
                Id = UuidV7.NewId(),
                UserId = foreignUser ? b.UserId : a.UserId,
                OwnerId = foreignUser ? a.SecondOwner : b.SecondOwner,
                RevokedAt = DateTime.UtcNow, // avoid the active-user unique index masking the FK
            });
            await db.SaveChangesAsync(ct);
        }, ct));
        ((PostgresException)error.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Multiple_users_can_share_an_owner_but_each_user_has_only_one_active_link()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await CreateLinkedUser(Roles.Owner, ct);
        var secondEmail = $"second-owner-{setup.UserId:N}@fixture.test";
        await AuthTestSupport.CreateUserAsync(fixture, setup.OrgId, secondEmail, "Second user", Roles.Owner, ct);
        await InOrg(setup.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var secondUser = await db.Users.SingleAsync(u => u.OrgId == setup.OrgId && u.Email == secondEmail, ct);
            await sp.GetRequiredService<OwnerAccessService>().GrantAsync(secondUser.Id, setup.FirstOwner, ct);
        }, ct);
        using var secondClient = await Login(secondEmail, AuthTestSupport.DefaultPassword, ct);
        (await ReadSummary(secondClient, ct)).OwnerName.ShouldBe("First owner");
        var error = await Should.ThrowAsync<DbUpdateException>(() => InOrg(setup.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(new OwnerAccess { Id = UuidV7.NewId(), UserId = setup.UserId, OwnerId = setup.SecondOwner });
            await db.SaveChangesAsync(ct);
        }, ct));
        ((PostgresException)error.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Link_rows_fail_closed_without_org_context_and_are_filtered_by_rls()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await CreateLinkedUser(Roles.Owner, ct);
        var b = await CreateLinkedUser(Roles.Owner, ct);
        await using var unscoped = fixture.CreateContext(fixture.AppConnectionString);
        (await unscoped.Set<OwnerAccess>().IgnoreQueryFilters().CountAsync(ct)).ShouldBe(0);
        await InOrg(a.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<OwnerAccess>().IgnoreQueryFilters().AnyAsync(l => l.UserId == b.UserId, ct)).ShouldBeFalse();
        }, ct);
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<OwnerAccessService>();
        await Should.ThrowAsync<InvalidOperationException>(() => service.GrantAsync(a.UserId, a.SecondOwner, ct));
    }

    [Fact]
    public async Task A_document_whose_bytes_are_missing_is_reported_unavailable_and_logged_but_only_to_its_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await CreateLinkedUser(Roles.Owner, ct);
        Guid ownMissing = UuidV7.NewId(), foreignMissing = UuidV7.NewId();
        await InOrg(setup.OrgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.AddRange(
                MissingBytesArtifact(ownMissing, setup.FirstOwner),
                MissingBytesArtifact(foreignMissing, setup.SecondOwner));
            await db.SaveChangesAsync(ct);
        }, ct);

        await using var host = fixture.Api.WithWebHostBuilder(_ => { });
        using var logs = new CapturingLoggerProvider();
        host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        using var client = host.CreateClient();
        await client.PrimeCsrfAsync(ct);
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(setup.Email, AuthTestSupport.DefaultPassword), ct))
            .EnsureSuccessStatusCode();

        var unavailable = await client.GetAsync(PdfPath(ownMissing), ct);
        unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        unavailable.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using (var problem = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync(ct)))
        {
            problem.RootElement.GetProperty("code").GetString().ShouldBe("statement_document_unavailable");
            problem.RootElement.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
            problem.RootElement.GetProperty("detail").GetString()!.ShouldNotContain(ownMissing.ToString());
        }

        logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning
            && e.EventId.Name == "OwnerStatementDocumentUnavailable" && e.Message.Contains(ownMissing.ToString()));

        // Another owner's artifact with missing bytes is still just not-found: no existence oracle.
        (await NotFoundShape(client, foreignMissing, ct)).ShouldBe(await NotFoundShape(client, UuidV7.NewId(), ct));

        // The list still names the issued document; it does not pretend the document was never issued.
        var statements = (await client.GetFromJsonAsync<OwnerPortalStatements>(StatementsPath, ct))!;
        statements.Statements.Select(s => s.Id).ShouldContain(ownMissing);
        statements.Statements.Select(s => s.Id).ShouldNotContain(foreignMissing);
    }

    [Fact]
    public async Task An_owner_with_no_activity_or_statements_reads_empty_collections()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await CreateLinkedUser(Roles.Owner, ct);
        await InOrg(setup.OrgId, sp => sp.GetRequiredService<OwnerAccessService>().RevokeAsync(setup.UserId, ct), ct);
        await InOrg(setup.OrgId, sp => sp.GetRequiredService<OwnerAccessService>().GrantAsync(setup.UserId, setup.SecondOwner, ct), ct);
        using var client = await Login(setup.Email, AuthTestSupport.DefaultPassword, ct);
        var summary = await ReadSummary(client, ct);
        summary.Balance.ShouldBe(0m);
        summary.Basis.ShouldBe("cash");
        summary.Activity.ShouldBeEmpty();
        summary.Disbursements.ShouldBeEmpty();
        (await client.GetFromJsonAsync<OwnerPortalStatements>(StatementsPath, ct))!.Statements.ShouldBeEmpty();
    }

    [Fact]
    public async Task Owner_categories_are_an_allowlist_that_never_echoes_an_event_type()
    {
        foreach (var eventType in new[] { "OwnerDisbursed", "PaymentReceived", "SomeFutureEvent", "EntryVoided" })
        {
            var category = OwnerPortalProjection.Category(eventType, isReversal: false);
            AllowedCategories.ShouldContain(category);
            if (eventType is "SomeFutureEvent" or "EntryVoided")
            {
                category.ShouldBe("Adjustment");
            }
        }

        OwnerPortalProjection.Category("OwnerDisbursed", isReversal: true).ShouldBe("Reversal");
        await Task.CompletedTask;
    }

    private static readonly HashSet<string> AllowedCategories =
    [
        "Rent", "Tenant charge", "Tenant payment", "Tenant credit", "Applied deposit", "Applied prepayment",
        "Management fee", "Vendor payment", "Owner contribution", "Owner disbursement", "Opening balance",
        "Reversal", "Adjustment",
    ];

    private static StatementArtifact MissingBytesArtifact(Guid id, Guid ownerId) => new()
    {
        Id = id,
        OwnerId = ownerId,
        PeriodYear = 2026,
        PeriodMonth = 8,
        Basis = "cash",
        EndingBalance = 0m,
        AsOf = DateTime.UtcNow,
        ArtifactKey = $"missing-{id:N}.pdf",
    };

    private static async Task<(HttpStatusCode Status, string Body)> NotFoundShape(HttpClient client, Guid artifactId, CancellationToken ct)
    {
        var response = await client.GetAsync(PdfPath(artifactId), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static void AssertProperties(string json, params string[] expected)
    {
        using var doc = JsonDocument.Parse(json);
        Names(doc.RootElement).ShouldBe(expected);
    }

    private static string[] Names(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    /// <summary>
    /// A new org with a user in <paramref name="role"/> linked to "First owner", a "Second owner", and
    /// one issued statement for the first owner with real bytes in the artifact store.
    /// </summary>
    private async Task<(Guid OrgId, Guid UserId, Guid FirstOwner, Guid SecondOwner, Guid FirstArtifact, string Email)> CreateLinkedUser(
        string role, CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        var email = $"owner-portal-{orgId:N}@fixture.test";
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Owner portal test", ct);
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "User", role, ct);
        Guid userId = Guid.Empty, first = UuidV7.NewId(), second = UuidV7.NewId(), artifact = UuidV7.NewId();
        await InOrg(orgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            userId = await db.Users.Where(u => u.OrgId == orgId && u.Email == email).Select(u => u.Id).SingleAsync(ct);
            db.AddRange(
                new Owner { Id = first, Name = "First owner" },
                new Owner { Id = second, Name = "Second owner" });
            var key = $"{artifact:N}.pdf";
            await sp.GetRequiredService<IArtifactStore>().PutAsync("%PDF-1.7 owner portal test"u8.ToArray(), key, ct);
            db.Add(new StatementArtifact
            {
                Id = artifact,
                OwnerId = first,
                PeriodYear = 2026,
                PeriodMonth = 9,
                Basis = "cash",
                EndingBalance = 0m,
                AsOf = DateTime.UtcNow,
                ArtifactKey = key,
            });
            await db.SaveChangesAsync(ct);
            await sp.GetRequiredService<OwnerAccessService>().GrantAsync(userId, first, ct);
        }, ct);
        return (orgId, userId, first, second, artifact, email);
    }

    private async Task InOrg(Guid orgId, Func<IServiceProvider, Task> action, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>()
            .RunAsSystemAsync(orgId, ProcessName, () => action(scope.ServiceProvider), ct);
    }

    private async Task<HttpClient> Login(string email, string password, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<OwnerPortalSummary> ReadSummary(HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync(SummaryPath, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<OwnerPortalSummary>(ct))!;
    }
}
