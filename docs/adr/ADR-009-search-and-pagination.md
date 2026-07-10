# ADR-009: Search via pg_trgm + GIN; consistent paged list contract

- **Status:** Accepted
- **Date:** 2026-06-13
- **Deciders:** Engineering

## Context

M2 introduces two read shapes that need a deliberate decision: the ⌘K **cross-entity search** (the
click-depth killer — jump to any owner/property/unit/tenant/bank in ≤ 2 interactions) and the **paged
list** contract every index screen shares. Both must hold at the Pro-tier ceiling (≤ 300 units, a few
hundred owners/tenants) with a sub-100 ms search target, and neither should pull in operational weight
the product does not yet warrant.

For search, the realistic options were:

1. **PostgreSQL `pg_trgm` (trigram) + GIN indexes** — fuzzy, typo-tolerant matching inside the database
   we already run, no new infrastructure.
2. **PostgreSQL full-text search (`tsvector`/`tsquery`)** — good for word/document search, but built for
   token/stemming matching, not the partial-prefix, typo-tolerant "type a few letters of a name"
   interaction a command palette needs ("hargrov" → "Hargrove").
3. **An external search engine (Elastic/OpenSearch/Meilisearch)** — powerful, but a whole service to
   run, secure, and keep in sync for a dataset that fits comfortably in Postgres.

## Decision

**Search uses `pg_trgm` word-similarity over GIN trigram indexes; no external engine, no tsvector.**

- The `AddDirectory` migration creates `CREATE EXTENSION pg_trgm` and a **GIN `gin_trgm_ops` index** on
  each searchable column (`owners.name`, `properties.address`, `units.label`, `tenants.display_name`,
  `bank_accounts.name`; §C.1).
- `GET /api/search?q=&limit=` runs a **typed UNION** across the five entity sources, filtering with the
  **word-similarity operator `<%`** (the query is fuzzy-matched as a _word/substring_ of the column) and
  ranking by `word_similarity(q, col)` desc, then label. `<%` is GIN-indexable via `gin_trgm_ops`, so
  short queries hit the index rather than scanning. The handler lowers `pg_trgm.word_similarity_threshold`
  with `SET LOCAL` for the request transaction only (it dies with the tx — safe, M-E11). Whole-string
  similarity (`%`) was rejected: "carter" has low similarity to "Jasmine Carter" but high _word_
  similarity, which is the interaction we want.
- Results are `{ type, id, label, sublabel, score }`, `WHERE NOT is_system` (aggregate rows never
  surface, P40/M2-E2), default limit 20 (max 50), `q` 1–100 chars (empty → 400 via the validation
  pipeline). Org scope rides the ambient RLS connection.

**Lists use one consistent paged contract** (§C.3 / P42): `PagedResponse<T> { items, total, page,
pageSize }` with query params `page` (1-based, default 1), `pageSize` (default 50, max 200), `q`
(free-text), `sort` (`field[:asc|desc]`). At demo/Pro scale the SPA loads one ample page and filters
**client-side instantly** (the budgeted-UX win, TODO M2.2), while the **server contract stays paginated**
so nothing changes when a tenant outgrows one page. List free-text filtering uses provider-agnostic
`lower(col) LIKE '%q%'` (the trigram indexes are reserved for the search endpoint, where fuzzy ranking
matters); a list is an exact-substring narrow, not a fuzzy rank.

## Consequences

- **No new infrastructure.** Search lives in the database we already operate, back up, and secure under
  RLS. One extension + five indexes, created by the same migration as the tables.
- **Typo-tolerant, fast at our scale.** Trigram GIN indexes make `<%` sub-100 ms for hundreds of rows;
  the palette feels instant. Ranking is good enough for "jump to the thing I'm typing".
- **One list shape everywhere.** Every index screen and future consumer reads the same envelope; client
  filtering keeps the budgeted flows instant without bespoke endpoints.
- **Costs accepted:** trigram relevance is cruder than a real search engine's (no stemming, synonyms, or
  multi-field boosting); the UNION-of-five query grows if many entity types are added. Both are fine for
  Phase 1's "find a record" need.

## Revisit trigger

If search **recall or latency degrades past the Pro-tier ceiling** — multi-word/semantic queries, many
more entity types, or datasets large enough that trigram GIN scans slow down — reconsider: first
`tsvector` for tokenized relevance, then a dedicated search service (Meilisearch/OpenSearch) with a
sync pipeline. The typed `SearchResult` contract and the `/api/search` shape stay stable across that
change; only the implementation behind the endpoint moves.
