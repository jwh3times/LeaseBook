using FluentValidation;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Search;

/// <summary>
/// Cross-entity fuzzy search powering the ⌘K palette (§C.5 / P43 / ADR-009): a typed union over
/// owners/properties/units/tenants/banks ranked by pg_trgm word-similarity. System rows excluded.
/// </summary>
public sealed record Search(string Q, int? Limit) : IQuery<IReadOnlyList<SearchResult>>;

public sealed record SearchResult(string Type, Guid Id, string Label, string? Sublabel, double Score);

public sealed class SearchValidator : AbstractValidator<Search>
{
    public SearchValidator()
    {
        RuleFor(x => x.Q).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Limit).InclusiveBetween(1, 50).When(x => x.Limit.HasValue);
    }
}

internal sealed class SearchHandler(DbContext db) : IQueryHandler<Search, IReadOnlyList<SearchResult>>
{
    public async Task<IReadOnlyList<SearchResult>> Handle(Search query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit ?? 20, 1, 50);
        var q = query.Q.Trim();

        // word_similarity (the `<%` operator) finds the query as a fuzzy *substring/word* of the column —
        // so "carter" matches "Jasmine Carter" where whole-string `%` similarity would not. `<%` is
        // GIN-indexable via the gin_trgm_ops indexes (§C.1), keeping this sub-100ms at Pro scale. The
        // threshold is lowered for this transaction only (SET LOCAL dies with the request tx, M-E11);
        // 0.3 catches partial words without flooding results.
        await db.Database.ExecuteSqlAsync($"SET LOCAL pg_trgm.word_similarity_threshold = 0.3", ct);

        // org scope + WHERE NOT is_system ride along: RLS scopes the ambient connection (M-E11); the
        // is_system filter keeps aggregate rows out (P40/M2-E2). bank_accounts has no is_system column.
        var rows = await db.Database.SqlQuery<SearchResult>(
            $"""
            SELECT type, id, label, sublabel, score FROM (
                SELECT 'owner' AS type, o.id, o.name AS label,
                       (SELECT count(*) FROM properties p WHERE p.owner_id = o.id)::text || ' properties' AS sublabel,
                       word_similarity({q}, o.name)::float8 AS score
                FROM owners o
                WHERE NOT o.is_system AND {q} <% o.name

                UNION ALL
                SELECT 'property', pr.id, pr.address,
                       COALESCE(ow.name, ''),
                       word_similarity({q}, pr.address)::float8
                FROM properties pr LEFT JOIN owners ow ON ow.id = pr.owner_id
                WHERE NOT pr.is_system AND {q} <% pr.address

                UNION ALL
                SELECT 'unit', u.id, u.label,
                       COALESCE(up.address, ''),
                       word_similarity({q}, u.label)::float8
                FROM units u LEFT JOIN properties up ON up.id = u.property_id
                WHERE NOT u.is_system AND {q} <% u.label

                UNION ALL
                SELECT 'tenant', t.id, t.display_name,
                       COALESCE((SELECT un.label FROM lease_lite l JOIN units un ON un.id = l.unit_id
                                 WHERE l.tenant_id = t.id AND l.status = 'active' LIMIT 1), ''),
                       word_similarity({q}, t.display_name)::float8
                FROM tenants t
                WHERE NOT t.is_system AND {q} <% t.display_name

                UNION ALL
                SELECT 'bank', b.id, b.name,
                       TRIM(COALESCE(b.institution, '') || COALESCE(' ••' || b.mask, '')),
                       word_similarity({q}, b.name)::float8
                FROM bank_accounts b
                WHERE {q} <% b.name
            ) results
            ORDER BY score DESC, label
            LIMIT {limit}
            """).ToListAsync(ct);

        return rows;
    }
}
