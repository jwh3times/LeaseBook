using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Reporting;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #468 no-leak proof: the portal fixture writes a staff-only internal note on every staff-typed entry,
/// and its voids carry staff-only reasons. None of that text may reach anything an owner or resident is
/// given — the owner statement's PDF text and CSV (both bases), the issued artifact the owner portal
/// serves, the owner-portal JSON, or the resident-portal JSON. A positive control first proves the notes
/// really are in the journal, so an absent note cannot pass for a withheld one.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class InternalNoteNoLeakTests(PostgresFixture fixture)
{
    // The fixture's note prefix, and the two void reasons it writes. Descriptions in the fixture are
    // owner-facing on purpose ("Internal rent memo", …) and legitimately print on a statement, so the scan
    // targets what is staff-only — none of these strings is part of any description.
    private static readonly string[] StaffOnly =
    [
        PortalSeeder.StaffNoteMarker, "correction reason", "void reason",
    ];

    [Fact]
    public async Task Internal_notes_never_reach_an_owner_or_resident_surface()
    {
        var ct = TestContext.Current.CancellationToken;
        await PortalSeeder.SeedAsync(fixture.Api.Services, ct);

        // ── Positive control: the notes are in the journal, on entries these surfaces do show ──────────
        var notes = await InPortalOrg(async sp => await sp.GetRequiredService<AppDbContext>().Set<JournalEntry>()
            .Where(e => e.InternalNote != null).Select(e => e.InternalNote!).ToListAsync(ct), ct);
        notes.ShouldContain(n => n.StartsWith(PortalSeeder.StaffNoteMarker, StringComparison.Ordinal));
        notes.ShouldContain("Internal correction reason");
        notes.ShouldContain("Internal disbursement void reason");

        // ── Owner statement: PDF text and CSV, both bases, rendered from the owner-statement read ──────
        var views = await InPortalOrg(async sp =>
        {
            var ownerA = await sp.GetRequiredService<AppDbContext>().Set<Owner>()
                .Where(o => o.ContactEmail == PortalSeeder.OwnerAEmail).Select(o => o.Id).SingleAsync(ct);
            var assembler = sp.GetRequiredService<StatementAssembler>();
            return new[]
            {
                (await assembler.BuildAsync([ownerA], null, 2026, 9, "cash", ct))[0],
                (await assembler.BuildAsync([ownerA], null, 2026, 9, "accrual", ct))[0],
            };
        }, ct);
        var renderedVoid = false;
        foreach (var view in views)
        {
            var lines = view.Sections.SelectMany(s => s.Lines).Select(l => l.Description).ToList();
            lines.ShouldContain(d => d.StartsWith("Void — ", StringComparison.Ordinal),
                $"the {view.Basis} statement must carry a void, or this test proves nothing about void reasons");

            var pdfText = PdfText(StatementPdf.Render(view));
            renderedVoid |= pdfText.Contains("Void —", StringComparison.Ordinal);
            AssertNoStaffText($"{view.Basis} statement PDF", pdfText);
            AssertNoStaffText($"{view.Basis} statement CSV", Encoding.UTF8.GetString(StatementCsv.Write(view)));
            AssertNoStaffText($"{view.Basis} statement JSON", JsonSerializer.Serialize(view));
        }

        // The accrual PDF itemizes the fee void (a disbursement void folds into the one disbursement row).
        renderedVoid.ShouldBeTrue("a rendered statement must print a void line, or its reason was never at risk");

        // ── Owner portal: JSON, and the issued artifact it serves ─────────────────────────────────────
        using (var owner = await Login(PortalSeeder.OwnerAEmail, ct))
        {
            AssertNoStaffText("owner-portal summary", await owner.GetStringAsync("/api/portal/owner/summary", ct));
            var statements = await owner.GetStringAsync("/api/portal/owner/statements", ct);
            AssertNoStaffText("owner-portal statements", statements);

            using var list = JsonDocument.Parse(statements);
            var artifacts = list.RootElement.GetProperty("statements").EnumerateArray()
                .Select(s => s.GetProperty("id").GetGuid()).ToList();
            artifacts.ShouldNotBeEmpty();
            foreach (var artifact in artifacts)
            {
                var pdf = await owner.GetAsync($"/api/portal/owner/statements/{artifact}/pdf", ct);
                pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
                var text = PdfText(await pdf.Content.ReadAsByteArrayAsync(ct));
                // The issued statement itemizes the fixture's payments, each of which carries a note.
                text.ShouldContain("Internal bank reference", Case.Sensitive,
                    "the issued statement must itemize an entry that has a note, or this proves nothing");
                AssertNoStaffText($"issued statement {artifact}", text);
            }
        }

        // ── Resident portal ──────────────────────────────────────────────────────────────────────────
        using (var resident = await Login(PortalSeeder.ResidentAEmail, ct))
        {
            AssertNoStaffText("resident-portal ledger", await resident.GetStringAsync("/api/portal/tenant/ledger", ct));
            AssertNoStaffText("resident-portal payments", await resident.GetStringAsync("/api/portal/tenant/payments", ct));
        }
    }

    /// <summary>
    /// The carry-forward half (ADR-045): entries posted into an already-issued period reach the owner
    /// through the next statement's prior-period adjustments, a separate read from the in-period lines.
    /// A noted charge and a void with a staff-only reason are both back-dated into an issued September,
    /// so October itemizes them there. Neither note may follow.
    /// </summary>
    [Fact]
    public async Task Internal_notes_never_reach_the_carry_forward_adjustments()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        Guid owner = default, property = default, tenant = default;
        const string ChargeNote = "STAFF-ONLY note: late-keyed charge";
        const string VoidReason = "STAFF-ONLY void reason: owner dispute";

        // September: one charge, then the September accrual statement is issued.
        Guid septemberCharge = default;
        await InOrg(orgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Orgs.Add(new OrgEntity { Id = orgId, Name = "Carry-forward note org" });
            var o = new Owner { Id = UuidV7.NewId(), Name = "Carry Owner", ContactEmail = "carry@example.com" };
            var p = new Property { Id = UuidV7.NewId(), OwnerId = o.Id, Address = "9 Carry Court" };
            var t = new Tenant { Id = UuidV7.NewId(), DisplayName = "Carry Tenant" };
            db.AddRange(o, p, t);
            await db.SaveChangesAsync(ct);
            await sp.GetRequiredService<IChartOfAccounts>().ProvisionAsync([], ct);
            (owner, property, tenant) = (o.Id, p.Id, t.Id);

            septemberCharge = await sp.GetRequiredService<IAccountingEvents>().PostAsync(new RentCharged(
                tenant, property, owner, null, new Money(40m), new DateOnly(2026, 9, 5), "Pet fee"), ct);
            var september = await sp.GetRequiredService<StatementAssembler>()
                .BuildAsync([owner], null, 2026, 9, "accrual", ct);
            await sp.GetRequiredService<IStatementDelivery>().DeliverAsync(september[0], "carry@example.com", ct);
            return 0;
        }, ct);

        // After issuance: a noted charge and a void, both dated into the issued September.
        await InOrg(orgId, async sp =>
        {
            await sp.GetRequiredService<IAccountingEvents>().PostAsync(new RentCharged(
                tenant, property, owner, null, new Money(100m), new DateOnly(2026, 9, 20), "Parking — September",
                InternalNote: ChargeNote), ct);
            await sp.GetRequiredService<IReversalService>().ReverseAsync(
                septemberCharge, VoidReason, new DateOnly(2026, 9, 25), ct);
            return 0;
        }, ct);

        var october = await InOrg(orgId, async sp =>
            (await sp.GetRequiredService<StatementAssembler>().BuildAsync([owner], null, 2026, 10, "accrual", ct))[0], ct);

        october.CarryForward.ShouldNotBeNull("both postings are dated into the issued September");
        october.CarryForward.Lines.Select(l => l.Description)
            .ShouldBe(["Parking — September", "Void — Pet fee"], ignoreOrder: true);
        var pdf = PdfText(StatementPdf.Render(october));
        pdf.ShouldContain("Parking", Case.Sensitive, "the carry-forward is rendered, or this proves nothing");
        foreach (var (surface, text) in new[]
        {
            ("carry-forward PDF", pdf),
            ("carry-forward CSV", Encoding.UTF8.GetString(StatementCsv.Write(october))),
            ("carry-forward JSON", JsonSerializer.Serialize(october)),
        })
        {
            text.ShouldNotContain("STAFF-ONLY", Case.Insensitive, $"{surface} leaked staff-only text");
            text.ShouldNotContain("owner dispute", Case.Insensitive, $"{surface} leaked the void reason");
        }
    }

    /// <summary>
    /// The notes overlay is a staff route: an owner is refused it even for their own statement, and so
    /// is a resident. Role denials stay bare 403s (see MiddlewareErrorContractTests), so only the status
    /// is asserted.
    /// </summary>
    [Theory]
    [InlineData(PortalSeeder.OwnerAEmail)]
    [InlineData(PortalSeeder.ResidentAEmail)]
    public async Task Portal_personas_are_refused_the_statement_notes_overlay(string email)
    {
        var ct = TestContext.Current.CancellationToken;
        await PortalSeeder.SeedAsync(fixture.Api.Services, ct);
        var ownerA = await InPortalOrg(async sp => await sp.GetRequiredService<AppDbContext>().Set<Owner>()
            .Where(o => o.ContactEmail == PortalSeeder.OwnerAEmail).Select(o => o.Id).SingleAsync(ct), ct);

        using var client = await Login(email, ct);
        var response = await client.GetAsync(
            $"/api/statements/{ownerA}/internal-notes?year=2026&month=9&basis=cash", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(ct)).ShouldNotContain(PortalSeeder.StaffNoteMarker);
    }

    private static void AssertNoStaffText(string surface, string text)
    {
        foreach (var secret in StaffOnly)
        {
            text.ShouldNotContain(secret, Case.Insensitive, $"{surface} leaked staff-only text \"{secret}\"");
        }
    }

    private static string PdfText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var text = new StringBuilder();
        foreach (Page page in document.GetPages())
        {
            foreach (Word word in page.GetWords())
            {
                text.Append(word.Text).Append(' ');
            }
        }

        return text.ToString();
    }

    private async Task<T> InOrg<T>(Guid orgId, Func<IServiceProvider, Task<T>> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        T result = default!;
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(
            orgId, "test:internal-note-no-leak", async () => result = await work(scope.ServiceProvider), ct);
        return result;
    }

    private async Task<T> InPortalOrg<T>(Func<IServiceProvider, Task<T>> read, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        T result = default!;
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(
            PortalSeeder.PortalOrgId, "test:internal-note-no-leak",
            async () => result = await read(scope.ServiceProvider), ct);
        return result;
    }

    private async Task<HttpClient> Login(string email, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, PortalSeeder.Password), ct))
            .EnsureSuccessStatusCode();
        return client;
    }
}
