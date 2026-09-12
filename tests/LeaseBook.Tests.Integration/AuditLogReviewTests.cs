using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Audit;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Endpoints;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The PMAdmin audit-review surface (#321): a filtered, paged read of the whole audited universe, with a
/// per-event payload diff behind it. Each test owns its org.
/// <para>
/// The tests that matter most here are the ones about what the surface is <b>for</b> — a directory write
/// the compliance extract deliberately drops, and a payload the product does not author — and the two
/// that pin how it can go quietly wrong: paging arithmetic, and an event from another org.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AuditLogReviewTests(PostgresFixture fixture)
{
    /// <summary>
    /// The reason this is a separate read rather than a parameter on the compliance extract. "Who changed
    /// this tenant's contact email" is a review question, and the extract answers it with silence because
    /// <c>tenants</c> is not money-touching.
    /// </summary>
    [Fact]
    public async Task Review_sees_a_directory_write_the_money_extract_drops()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        await WriteTenantAsync(orgId, "Jasmine Carter", ct);

        var review = await ReadAsync(orgId, new AuditLogFilters(EntityType: "tenants"), ct);
        review.Rows.ShouldNotBeEmpty();
        review.Rows.ShouldAllBe(r => r.EntityType == "tenants");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var extract = await AsSystemAsync(orgId, (sp, c) =>
            new AuditExtractReader(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IOrgContext>())
                .GetAsync(today.AddDays(-1), today.AddDays(1), c), ct);

        extract.Rows.ShouldNotContain(r => r.EntityType == "tenants");
    }

    [Fact]
    public async Task Entity_type_and_action_filters_narrow_the_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var tenantId = await WriteTenantAsync(orgId, "Jasmine Carter", ct);
        await WriteBankAsync(orgId, "Operating Trust", ct);
        await UpdateTenantEmailAsync(orgId, tenantId, "jasmine@example.com", ct);

        var all = await ReadAsync(orgId, new AuditLogFilters(), ct);
        all.Total.ShouldBeGreaterThanOrEqualTo(3);

        var tenants = await ReadAsync(orgId, new AuditLogFilters(EntityType: "tenants"), ct);
        tenants.Rows.ShouldAllBe(r => r.EntityType == "tenants");
        tenants.Total.ShouldBe(2); // the insert and the update

        var updates = await ReadAsync(orgId, new AuditLogFilters(EntityType: "tenants", Action: "update"), ct);
        updates.Total.ShouldBe(1);
        updates.Rows.Single().EntityId.ShouldBe(tenantId);
    }

    /// <summary>
    /// Page 1 then page 2 must return each row once, in the order one page would have given. This gates the
    /// offset arithmetic — regressed by one, it goes red on the duplicate.
    /// <para>
    /// It does <b>not</b> gate the id tiebreak, which is the other thing that makes paging total. That was
    /// the intent when this test was written; run against a build with the tiebreak removed, it stayed
    /// green, because Postgres returns the same order for both queries anyway. The tiebreak is guarded by
    /// <see cref="Review_order_breaks_ties_on_id"/> instead, which asserts the SQL, and the eight rows at
    /// one timestamp are kept here because that is the shape the paging arithmetic has to survive.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Paging_neither_repeats_nor_skips_a_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        // Eight tenants in one SaveChanges: eight audit rows, one timestamp.
        await AsSystemAsync(orgId, async (sp, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 8; i++)
            {
                db.Add(NewTenant(orgId, $"Tied Tenant {i}"));
            }

            await db.SaveChangesAsync(c);
            return 0;
        }, ct);

        var first = await ReadAsync(orgId, new AuditLogFilters(EntityType: "tenants", Page: 1, PageSize: 5), ct);
        var second = await ReadAsync(orgId, new AuditLogFilters(EntityType: "tenants", Page: 2, PageSize: 5), ct);

        first.Total.ShouldBe(8);
        first.Rows.Count.ShouldBe(5);
        second.Rows.Count.ShouldBe(3);

        var paged = first.Rows.Concat(second.Rows).Select(r => r.Id).ToList();
        paged.Distinct().Count().ShouldBe(8, "paging repeated a row it had already returned");

        var everything = await ReadAsync(orgId, new AuditLogFilters(EntityType: "tenants", PageSize: 50), ct);
        paged.ShouldBe(everything.Rows.Select(r => r.Id).ToList(), "paging returned a different order than one page did");
    }

    /// <summary>
    /// The review order is total: <c>occurred_at</c> descending, then <c>id</c>. Asserted against the SQL
    /// rather than against returned rows because the tiebreak has no observable behaviour — Postgres is
    /// free to break a tie either way and, for these queries, breaks it the same way with or without the
    /// clause. A behavioural test would pass on the regression, which is the same as no test at all.
    /// </summary>
    [Fact]
    public async Task Review_order_breaks_ties_on_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        var sql = await AsSystemAsync(orgId, (sp, _) => Task.FromResult(
            AuditLogReader.InReviewOrder(sp.GetRequiredService<AppDbContext>().AuditEvents).ToQueryString()), ct);

        var orderBy = sql[sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)..];
        orderBy.Contains("occurred_at DESC", StringComparison.Ordinal)
            .ShouldBeTrue($"the review order no longer leads with occurred_at: {orderBy}");
        orderBy.Contains(".id DESC", StringComparison.Ordinal)
            .ShouldBeTrue(
                "the review order lost its id tiebreak, so paging over rows that share a timestamp is at " +
                $"the mercy of the query plan — see AuditLogReader.InReviewOrder: {orderBy}");
    }

    /// <summary>
    /// The point of the drawer: an update reports the one column that moved, not the twenty that did not.
    /// A full snapshot is technically the truth and practically unreadable.
    /// </summary>
    [Fact]
    public async Task An_update_reports_only_the_field_that_moved()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var tenantId = await WriteTenantAsync(orgId, "Jasmine Carter", ct);
        await UpdateTenantEmailAsync(orgId, tenantId, "jasmine@example.com", ct);

        var updates = await ReadAsync(orgId, new AuditLogFilters(EntityType: "tenants", Action: "update"), ct);
        var detail = await ReadDetailAsync(orgId, updates.Rows.Single().Id, ct);

        detail.ShouldNotBeNull();
        var change = detail.Changes.ShouldHaveSingleItem();
        change.Field.ShouldBe(nameof(Tenant.ContactEmail));
        change.Before.ShouldBeNull();
        change.After.ShouldBe("jasmine@example.com");
        change.Redacted.ShouldBeFalse();
    }

    [Fact]
    public async Task An_insert_reports_the_whole_row_and_has_no_before_side()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        await WriteTenantAsync(orgId, "Jasmine Carter", ct);

        var inserts = await ReadAsync(orgId, new AuditLogFilters(EntityType: "tenants", Action: "insert"), ct);
        var detail = await ReadDetailAsync(orgId, inserts.Rows.Single().Id, ct);

        detail.ShouldNotBeNull();
        detail.Changes.ShouldAllBe(c => c.Before == null);
        detail.Changes.ShouldContain(c => c.Field == nameof(Tenant.DisplayName) && c.After == "Jasmine Carter");
    }

    /// <summary>
    /// <c>ImportRow.RawJson</c> holds the customer's previous system's export verbatim — columns LeaseBook
    /// neither chose nor validated. The drawer withholds it rather than republishing it, and says it did:
    /// "you may not read this" and "this was cleared" are different facts, so the marker is not an empty
    /// string.
    /// </summary>
    [Fact]
    public void A_payload_the_product_does_not_author_is_withheld_and_says_so()
    {
        var changes = AuditLogReader.Diff(
            before: null,
            after: """{"Id":"7b1","RawJson":"{\"ssn\":\"000-00-0000\"}","RowNumber":3}""");

        var raw = changes.Single(c => c.Field == "RawJson");
        raw.Redacted.ShouldBeTrue();
        raw.After.ShouldBe(AuditFieldRedaction.Marker);
        raw.After.ShouldNotBeNullOrEmpty();

        // Everything around it still reads normally — redaction is per field, not per event.
        changes.ShouldContain(c => c.Field == "RowNumber" && c.After == "3" && !c.Redacted);
    }

    /// <summary>
    /// A payload that is not a flat column snapshot is withheld whole rather than dumped raw. The drawer
    /// explains fields; anything else is content it cannot vouch for.
    /// </summary>
    [Fact]
    public void A_payload_that_is_not_a_flat_object_is_withheld_whole()
    {
        var changes = AuditLogReader.Diff(before: null, after: """["not","an","object"]""");

        var only = changes.ShouldHaveSingleItem();
        only.Field.ShouldBe(AuditLogReader.UnreadablePayloadField);
        only.Redacted.ShouldBeTrue();
        only.After.ShouldBe(AuditFieldRedaction.Marker);
    }

    /// <summary>
    /// RLS, not a filter the reader could forget. The detail read is the sharper half: an id is guessable
    /// in a way a list is not, and a cross-org id must read exactly like one that never existed.
    /// </summary>
    [Fact]
    public async Task Another_orgs_events_are_neither_listed_nor_readable()
    {
        var ct = TestContext.Current.CancellationToken;
        var theirs = await NewOrgAsync(ct);
        var mine = await NewOrgAsync(ct);
        await WriteTenantAsync(theirs, "Their Tenant", ct);

        var theirEvents = await ReadAsync(theirs, new AuditLogFilters(EntityType: "tenants"), ct);
        var theirEventId = theirEvents.Rows.First().Id;

        var myView = await ReadAsync(mine, new AuditLogFilters(), ct);
        myView.Total.ShouldBe(0);
        myView.Rows.ShouldBeEmpty();

        (await ReadDetailAsync(mine, theirEventId, ct)).ShouldBeNull();
    }

    /// <summary>
    /// <c>asp_net_users</c> carries no RLS (the identity soft-spot, M3-E6), so the explicit org filter
    /// in the actor lookup is the <b>only</b> thing keeping another org's people out of this read. The
    /// case is constructible rather than hypothetical: the auditing pass validates the row's org but
    /// never that the declared actor belongs to it, so an org-A row can carry an org-B user id.
    /// <para>
    /// The row must then say "Unknown user", not "System". The id resolves to nothing, but
    /// <c>actor_kind</c> still says a person acted — and calling it a system write would be a false
    /// attribution that the System filter, which keys on <c>actor_kind</c>, would then refuse to return.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_actor_from_another_org_is_never_named()
    {
        var ct = TestContext.Current.CancellationToken;
        var mine = await NewOrgAsync(ct);
        var theirs = await NewOrgAsync(ct);
        var (theirUserId, theirEmail) = await CreateUserAsync(theirs, "Their Person", admin: false, ct);

        await AsUserAsync(mine, theirUserId, async (sp, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(NewTenant(mine, "Cross-org Write"));
            await db.SaveChangesAsync(c);
            return 0;
        }, ct);

        var row = (await ReadAsync(mine, new AuditLogFilters(EntityType: "tenants"), ct)).Rows.ShouldHaveSingleItem();

        row.ActorName.ShouldNotBe("Their Person");
        row.ActorEmail.ShouldBeNull();
        row.ActorName.ShouldNotBe("System", "the row records a user actor; calling it a system write is a false attribution");
        row.ActorName.ShouldBe("Unknown user");
        (await ReadAsync(mine, new AuditLogFilters(), ct)).Rows
            .ShouldNotContain(r => r.ActorEmail == theirEmail);
    }

    /// <summary>
    /// The same boundary on the other identity read. The filter's actor list is built from the identity
    /// table directly, so without its org filter the dropdown would name every user in the database.
    /// </summary>
    [Fact]
    public async Task The_actor_filter_lists_only_this_orgs_people()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mine, myEmail) = await OrgWithUserAsync(admin: true, ct);
        var theirs = await NewOrgAsync(ct);
        var (_, theirEmail) = await CreateUserAsync(theirs, "Their Person", admin: false, ct);

        var options = await AsSystemAsync(mine, (sp, c) =>
            sp.GetRequiredService<AuditLogReader>().GetFilterOptionsAsync(c), ct);

        options.Actors.ShouldContain(a => a.Email == myEmail);
        options.Actors.ShouldNotContain(a => a.Email == theirEmail);
        options.Actors.ShouldNotContain(a => a.Name == "Their Person");
    }

    // ---- Endpoint gating and contract ------------------------------------------------------------

    [Fact]
    public async Task PMStaff_without_admin_is_forbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, email) = await OrgWithUserAsync(admin: false, ct);
        var client = await LoggedInClientAsync(email, ct);

        (await client.GetAsync("/api/audit/events", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/audit/filters", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/audit/events.csv", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // The whole surface, not a sample — and this is the one route that returns a payload.
        (await client.GetAsync($"/api/audit/events/{UuidV7.NewId()}", ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PMAdmin_reads_the_trail_and_exports_the_same_filtered_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, email) = await OrgWithUserAsync(admin: true, ct);
        await WriteTenantAsync(orgId, "Jasmine Carter", ct);
        var client = await LoggedInClientAsync(email, ct);

        var listed = await client.GetFromJsonAsync<AuditLogResponse>("/api/audit/events?entityType=tenants", ct);
        listed!.Rows.ShouldNotBeEmpty();

        var export = await client.GetAsync("/api/audit/events.csv?entityType=tenants", ct);
        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");

        var csv = await export.Content.ReadAsStringAsync(ct);
        csv.ShouldContain("tenants");
        // Metadata only: the export can never carry a payload the drawer would have redacted.
        csv.ShouldNotContain("Jasmine Carter");
    }

    /// <summary>
    /// The export carries more than one browse page. It asks for <c>AuditLogCsv.MaxRows</c> rows, and
    /// that number only means anything if the reader honours it: the filters clamp a page size to
    /// <c>AuditLogFilters.MaxPageSize</c>, so the first version of this cut every export to 200 rows —
    /// under a header that announced a ten-thousand-row limit and no truncation.
    /// </summary>
    [Fact]
    public async Task The_export_is_not_cut_to_a_browse_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, email) = await OrgWithUserAsync(admin: true, ct);
        var wanted = AuditLogFilters.MaxPageSize + 25;

        await AsSystemAsync(orgId, async (sp, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            for (var i = 0; i < wanted; i++)
            {
                db.Add(NewTenant(orgId, $"Export Tenant {i}"));
            }

            await db.SaveChangesAsync(c);
            return 0;
        }, ct);

        var client = await LoggedInClientAsync(email, ct);
        var export = await client.GetAsync("/api/audit/events.csv?entityType=tenants", ct);
        var csv = await export.Content.ReadAsStringAsync(ct);

        // Title row + header row + one row per event.
        var dataRows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 2;
        dataRows.ShouldBe(wanted);
        csv.ShouldNotContain("truncated", Case.Insensitive);
    }

    /// <summary>
    /// Taking a copy of the whole organization's trail is itself recorded — attributed to the person
    /// who took it, and carrying the narrowing they used rather than a second copy of the rows.
    /// </summary>
    [Fact]
    public async Task Exporting_the_trail_is_itself_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, email) = await OrgWithUserAsync(admin: true, ct);
        await WriteTenantAsync(orgId, "Jasmine Carter", ct);
        var client = await LoggedInClientAsync(email, ct);

        (await client.GetAsync("/api/audit/events.csv?entityType=tenants", ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var recorded = await ReadAsync(
            orgId, new AuditLogFilters(EntityType: AuditLogEndpoints.ExportAuditEntityType), ct);
        var row = recorded.Rows.ShouldHaveSingleItem();
        row.ActorEmail.ShouldBe(email, "an export is attributed to the person who took it, not to the system");

        var detail = await ReadDetailAsync(orgId, row.Id, ct);
        detail.ShouldNotBeNull();
        detail.Changes.ShouldContain(c => c.Field == "entityType" && c.After == "tenants");
        detail.Changes.ShouldContain(c => c.Field == "exported" && c.After == "1");
        detail.Changes.ShouldNotContain(
            c => c.Field == "rows", "the payload records the narrowing, not a second copy of the trail");
    }

    [Fact]
    public async Task The_filter_vocabulary_covers_tables_and_hand_written_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, email) = await OrgWithUserAsync(admin: true, ct);
        var client = await LoggedInClientAsync(email, ct);

        var options = await client.GetFromJsonAsync<AuditFilterOptions>("/api/audit/filters", ct);

        options!.EntityTypes.ShouldContain("tenants");
        options.EntityTypes.ShouldContain("journal_entries");
        options.EntityTypes.ShouldContain("account-security");
        options.EntityTypes.ShouldNotContain("audit_events", "audit rows are never themselves audited");
        options.Actions.ShouldContain("update");
        options.Actions.ShouldContain("mfa-reset");
        options.Actors.ShouldContain(a => a.Email == email);
    }

    [Fact]
    public async Task An_actor_that_is_neither_a_person_nor_the_system_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, email) = await OrgWithUserAsync(admin: true, ct);
        var client = await LoggedInClientAsync(email, ct);

        var response = await client.GetAsync("/api/audit/events?actor=somebody", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemWithTitle>(ct);
        problem!.Title.ShouldBe("audit_actor_invalid");
    }

    /// <summary>
    /// Attribution is the review surface's whole subject, so a user write and a process write must be
    /// separable — and a filter on one must not return the other.
    /// </summary>
    [Fact]
    public async Task A_persons_writes_and_the_systems_writes_filter_apart()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var (userId, _) = await CreateUserAsync(orgId, "Renée Calloway", admin: true, ct);

        await AsUserAsync(orgId, userId, async (sp, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(NewTenant(orgId, "Signed-in Write"));
            await db.SaveChangesAsync(c);
            return 0;
        }, ct);
        await WriteTenantAsync(orgId, "Automated Write", ct); // system actor: "test-harness"

        var byPerson = await ReadAsync(orgId, new AuditLogFilters(ActorUserId: userId), ct);
        var bySystem = await ReadAsync(orgId, new AuditLogFilters(SystemActorsOnly: true), ct);

        byPerson.Rows.ShouldHaveSingleItem().ActorName.ShouldBe("Renée Calloway");
        bySystem.Rows.ShouldHaveSingleItem().ActorName.ShouldBe("System (test-harness)");
    }

    // ---- Harness ---------------------------------------------------------------------------------

    private sealed record ProblemWithTitle(string Title);

    private static Tenant NewTenant(Guid orgId, string displayName) => new()
    {
        Id = UuidV7.NewId(),
        OrgId = orgId,
        DisplayName = displayName,
        LifecycleStatus = TenantLifecycleStatus.Current,
    };

    private Task<AuditLogResponse> ReadAsync(Guid orgId, AuditLogFilters filters, CancellationToken ct) =>
        AsSystemAsync(orgId, (sp, c) => sp.GetRequiredService<AuditLogReader>().GetAsync(filters, c), ct);

    private Task<AuditEventDetail?> ReadDetailAsync(Guid orgId, Guid eventId, CancellationToken ct) =>
        AsSystemAsync(orgId, (sp, c) => sp.GetRequiredService<AuditLogReader>().GetDetailAsync(eventId, c), ct);

    private async Task<Guid> WriteTenantAsync(Guid orgId, string displayName, CancellationToken ct)
    {
        var tenant = NewTenant(orgId, displayName);
        await AsSystemAsync(orgId, async (sp, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(tenant);
            await db.SaveChangesAsync(c);
            return 0;
        }, ct);
        return tenant.Id;
    }

    private Task WriteBankAsync(Guid orgId, string name, CancellationToken ct) =>
        AsSystemAsync(orgId, async (sp, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(new BankAccount { Id = UuidV7.NewId(), OrgId = orgId, Name = name, Purpose = BankPurpose.Trust });
            await db.SaveChangesAsync(c);
            return 0;
        }, ct);

    private Task UpdateTenantEmailAsync(Guid orgId, Guid tenantId, string email, CancellationToken ct) =>
        AsSystemAsync(orgId, async (sp, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var tenant = await db.Set<Tenant>().SingleAsync(t => t.Id == tenantId, c);
            tenant.ContactEmail = email;
            await db.SaveChangesAsync(c);
            return 0;
        }, ct);

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Audit Review Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    private async Task<(Guid OrgId, string Email)> OrgWithUserAsync(bool admin, CancellationToken ct)
    {
        var orgId = await NewOrgAsync(ct);
        var (_, email) = await CreateUserAsync(orgId, "Renée Calloway", admin, ct);
        return (orgId, email);
    }

    private async Task<(Guid Id, string Email)> CreateUserAsync(
        Guid orgId, string displayName, bool admin, CancellationToken ct)
    {
        var email = $"user-{orgId:N}@example.com";
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            Id = UuidV7.NewId(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            OrgId = orgId,
            DisplayName = displayName,
        };
        (await userManager.CreateAsync(user, AuthTestSupport.DefaultPassword)).Succeeded.ShouldBeTrue();
        (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        if (admin)
        {
            (await userManager.AddToRoleAsync(user, Roles.PMAdmin)).Succeeded.ShouldBeTrue();
        }

        return (user.Id, email);
    }

    private async Task<HttpClient> LoggedInClientAsync(string email, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        (await AuthTestSupport.LoginAsync(client, email, ct)).Status.ShouldBe("ok");
        await client.PrimeCsrfAsync(ct);
        return client;
    }

    private async Task<T> AsSystemAsync<T>(
        Guid orgId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        T result = default!;
        await executor.RunAsSystemAsync(orgId, "test-harness", async () => result = await work(sp, ct), ct);
        return result;
    }

    private async Task<T> AsUserAsync<T>(
        Guid orgId, Guid userId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var executor = sp.GetRequiredService<OrgScopedExecutor>();
        T result = default!;
        await executor.RunAsync(orgId, Actor.User(userId), async () => result = await work(sp, ct), ct);
        return result;
    }
}
