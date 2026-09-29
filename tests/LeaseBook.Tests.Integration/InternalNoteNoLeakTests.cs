using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Directory.Domain;
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
