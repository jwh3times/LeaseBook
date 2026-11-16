using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #782: the owner page's Edit sends the owner back whole, because <c>UpdateOwner</c> replaces every
/// field. A dialog can only send back what the read gave it, so the owner read must carry every field
/// the update takes — initials included, which it once left out, so that saving any edit wiped them.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OwnerEditRoundTripTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_owner_read_returns_every_field_the_update_replaces_so_an_edit_keeps_the_initials()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, client) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMStaff, ct);
        var created = await client.PostAsJsonAsync("/api/directory/owners", new
        {
            name = "Hargrove Family Trust",
            initials = "HFT",
            contactEmail = "trustee@example.test",
            contactPhone = "919-555-0142",
            defaultMgmtFeeBps = 833,
            reserveAmount = 250m,
        }, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var read = await client.GetFromJsonAsync<JsonElement>($"/api/directory/owners/{id}", ct);
        read.GetProperty("initials").GetString().ShouldBe("HFT");

        // An edit of the name alone, built from the read the way the dialog builds it.
        var contact = read.GetProperty("contact");
        var saved = await client.PutAsJsonAsync($"/api/directory/owners/{id}", new
        {
            id,
            name = "Hargrove Family Trust II",
            initials = read.GetProperty("initials").GetString(),
            contactEmail = contact.GetProperty("email").GetString(),
            contactPhone = contact.GetProperty("phone").GetString(),
            defaultMgmtFeeBps = read.GetProperty("defaultMgmtFeeBps").GetInt32(),
            reserveAmount = read.GetProperty("reserveAmount").GetDecimal(),
        }, ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/directory/owners/{id}", ct);
        after.GetProperty("name").GetString().ShouldBe("Hargrove Family Trust II");
        after.GetProperty("initials").GetString().ShouldBe("HFT");
        after.GetProperty("contact").GetProperty("email").GetString().ShouldBe("trustee@example.test");
        after.GetProperty("contact").GetProperty("phone").GetString().ShouldBe("919-555-0142");
        after.GetProperty("defaultMgmtFeeBps").GetInt32().ShouldBe(833);
        after.GetProperty("reserveAmount").GetDecimal().ShouldBe(250m);
    }
}
