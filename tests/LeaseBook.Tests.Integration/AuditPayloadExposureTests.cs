using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Audit;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// What the audit-review drawer (#321) is allowed to render. The auditing pass snapshots <b>every</b>
/// column of an <see cref="IOrgScoped"/> entity into <c>before</c>/<c>after</c>, so the set of things a
/// reviewer can read is decided by which entities are org-scoped — a decision made far from this surface,
/// usually by someone adding a column.
/// <para>
/// These guards read the EF model, so they see the entities as they actually are rather than as the
/// redaction policy assumes. They fail when a new column changes what the drawer would show, which is the
/// moment the question is cheap to answer.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AuditPayloadExposureTests(PostgresFixture fixture)
{
    /// <summary>
    /// Audited <c>*Json</c> columns whose content LeaseBook itself authors — a reconciliation snapshot, a
    /// bulk-run summary, a mapping profile, a verification figure. Reviewed and rendered as-is, because a
    /// reviewer asking what a bulk run did is asking for exactly this.
    /// <para>
    /// The ones absent from this list are absent on purpose: <c>ImportRow</c>'s three carry the customer's
    /// previous system's export verbatim, which is content nobody here chose, and
    /// <see cref="AuditFieldRedaction.UnboundedContentFields"/> withholds them.
    /// </para>
    /// </summary>
    private static readonly string[] ReviewedJsonFields =
    [
        "ActualJson",
        "ColumnMapJson",
        "ExpectedJson",
        "SnapshotJson",
        "SummaryJson",
    ];

    [Fact]
    public void Every_audited_json_column_is_either_reviewed_or_redacted()
    {
        var audited = AuditedProperties();
        audited.Count.ShouldBeGreaterThan(100, "the model scan found almost nothing — it is broken, not the model");

        var unclassified = audited
            .Where(p => p.Property.EndsWith("Json", StringComparison.Ordinal))
            .Where(p => !ReviewedJsonFields.Contains(p.Property, StringComparer.Ordinal))
            .Where(p => !AuditFieldRedaction.IsRedacted(p.Property))
            .ToList();

        unclassified.ShouldBeEmpty(
            "these audited columns hold free-form JSON that the audit-review drawer would render to a " +
            "person. Decide which they are: LeaseBook-authored content (add to ReviewedJsonFields) or " +
            "content that came from outside (add to AuditFieldRedaction.UnboundedContentFields): " +
            $"{Describe(unclassified)}");
    }

    /// <summary>
    /// Identity is not org-scoped, deliberately (pitfall E6) — which is also why no password hash,
    /// security stamp or recovery code has ever reached <c>audit_events</c>. Making an Identity type
    /// org-scoped would start writing credentials into a payload the drawer reads out loud, and the
    /// tenancy reason for the split would not obviously mention it. This is where that shows up.
    /// <para>
    /// The store to worry about is <c>asp_net_user_tokens</c>, not <c>asp_net_users</c>: the TOTP
    /// authenticator key and the 2FA recovery codes live in <c>IdentityUserToken.Value</c>, which
    /// derives from neither <c>IdentityUser</c> nor <c>IdentityRole</c> and is named <c>Value</c>, so
    /// no name fragment would redact it. Its <c>EncryptedStringConverter</c> would not help either —
    /// the auditing pass snapshots <b>model</b> values, and the converter runs at the provider
    /// boundary below that, so the plaintext key is what would be written. Hence a namespace test
    /// rather than a base-class one.
    /// </para>
    /// </summary>
    [Fact]
    public void No_identity_type_is_audited()
    {
        var identityTypes = AuditedEntities()
            .Where(IsIdentityType)
            .Select(type => type.Name)
            .ToList();

        identityTypes.ShouldBeEmpty(
            "an ASP.NET Identity type became org-scoped, so every credential column on it is now " +
            "snapshotted into audit_events and rendered by the audit-review drawer: " +
            $"{string.Join(", ", identityTypes)}");
    }

    /// <summary>
    /// The guard above can only fail if it can recognise an Identity type at all. <c>AppUser</c> is
    /// one and is (correctly) not audited, so it is the fixture that proves the predicate works
    /// rather than returning false for everything.
    /// </summary>
    [Fact]
    public void The_identity_check_recognises_the_types_it_is_looking_for()
    {
        IsIdentityType(typeof(AppUser)).ShouldBeTrue("AppUser derives from IdentityUser<Guid>");
        IsIdentityType(typeof(IdentityUserToken<Guid>)).ShouldBeTrue("the TOTP key and recovery codes live here");
        IsIdentityType(typeof(IdentityUserLogin<Guid>)).ShouldBeTrue();
        IsIdentityType(typeof(Tenant)).ShouldBeFalse("a domain entity is not an Identity type");
    }

    /// <summary>
    /// A column whose name says "secret" is masked by <see cref="AuditFieldRedaction"/> from the day it
    /// lands — but masking it is the fallback, not the decision. The decision is whether a table holding
    /// one should be audited at all, and this is the prompt to make it.
    /// </summary>
    [Fact]
    public void No_audited_column_looks_like_a_secret()
    {
        var secrets = AuditedProperties()
            .Where(p => AuditFieldRedaction.SensitiveNameFragments
                .Any(fragment => p.Property.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        secrets.ShouldBeEmpty(
            "an audited entity gained a secret-shaped column. Its value is already withheld by name, " +
            "but decide whether the row should be audited at all before relying on that: " +
            $"{Describe(secrets)}");
    }

    /// <summary>
    /// Anything from ASP.NET Identity, by namespace rather than by base class — the six stores do not
    /// share one. Walks the base chain so <c>AppUser</c>, which is ours, is caught through
    /// <c>IdentityUser&lt;Guid&gt;</c>.
    /// </summary>
    private static bool IsIdentityType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Namespace == typeof(IdentityUser<>).Namespace)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Entity types the auditing pass snapshots — every org-scoped one but the audit log itself.</summary>
    private IReadOnlyList<Type> AuditedEntities()
    {
        using var scope = fixture.Api.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;
        return
        [
            .. model.GetEntityTypes()
                .Select(entity => entity.ClrType)
                .Where(type => typeof(IOrgScoped).IsAssignableFrom(type))
                .Where(type => type != typeof(AuditEvent))
                .Distinct(),
        ];
    }

    private IReadOnlyList<(string Entity, string Property)> AuditedProperties()
    {
        using var scope = fixture.Api.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;
        return
        [
            .. model.GetEntityTypes()
                .Where(entity => typeof(IOrgScoped).IsAssignableFrom(entity.ClrType))
                .Where(entity => entity.ClrType != typeof(AuditEvent))
                .SelectMany(entity => entity.GetProperties().Select(p => (entity.ClrType.Name, p.Name)))
                .Distinct(),
        ];
    }

    private static string Describe(IEnumerable<(string Entity, string Property)> properties) =>
        string.Join(", ", properties.Select(p => $"{p.Entity}.{p.Property}"));
}
