# Milestone 2 — Retro (Directory & the Index Layer)

> Companion to `m2_plan.md`. M3 planning starts here.

## Outcome

All 10 WPs + the §D gate are complete on branch `m2/directory` (off `main`). A person can now log in
as Renée, see the live dashboard hero at 0 clicks, ⌘K to any entity in ≤ 2 interactions, browse
tenants/owners/properties with instant filter + keyboard nav, open detail pages, create records, search,
and edit org settings. No money moves yet (the inline ledger composer is M3).

**Verification (gate):** reset-db → migrate-from-blank → `seed --org demo` ×2 (idempotent) →
`check-invariants --org demo` → **exit 0**. Full **.NET suite 123** (SharedKernel 26, Architecture 6,
Accounting 51, Integration 40), **web 45**, **Playwright e2e 5** all green; `dotnet format` clean. API
smoke against the seeded host ties to the M1 golden figures: 8 named owners (Hargrove operating
14,820.50 / deposit 8,400.00), dashboard trustTotal **483,620.69** + ownersPayable **111,967.40** (P41) +
banks sum == trustTotal + "All other owners" roll-up, search "carter" → Jasmine first, tenant balances
(Carter 1,450 / Mercer −75 / Vasquez 2,820). ADR-007/008/009 recorded; `schema.d.ts` regenerated.

## Deviations from the plan (all documented in commits/ADRs)

1. **Seed row-materialization pulled forward WP-06 → WP-01.** P38 puts the five `journal_lines`→
   directory FKs in the `AddDirectory` migration (WP-01), but the directory seed was scheduled for WP-06
   — which would leave `DemoSeeder`/`SeederTests` red across waves 1–3. To honor P38 *and* keep the full
   suite green at every commit, `DemoDirectorySeed` (owners/properties/tenants/banks/org_settings, incl.
   the `is_system` aggregates) landed in WP-01, before the journal replay. WP-06 kept its real value: the
   units/leases, the **golden-join proof** (figures tie to names through the WP-03/05 reads), idempotency
   assertions, and the TS client regen. ADR-008 documents the coupling.
2. **Engine-test FK-target seeding (the one design fork you weighed in on).** The new journal FKs require
   a directory row per non-null dimension, which the M1 Accounting engine tests violated (synthetic /
   random dims, incl. two property-based suites). Resolved by seeding FK-target rows via the **migrator
   with `ON CONFLICT DO NOTHING` in a hidden harness org** — leveraging that **Postgres FK checks bypass
   RLS**, so one global row per id satisfies every test org (the fixed dims reused across orgs would
   otherwise collide on the global PK). No production/posting code changed. Touched ~9 M1 test files +
   the harness + one M1 round-trip test.
3. **Small additive design-layer changes** (within "compose, don't restyle"): `Table.selectedKey` (keyboard
   selection), `Topbar.onSearchClick` (live ⌘K affordance), and a scoped `design/m2.css` (detail/dashboard/
   palette/modal styles, reusing existing tokens only).
4. **Additive read slices** beyond the named ones: Accounting `GetTenantBalances` (batch, for the tenant
   list port — GetTenantLedger is per-id and M2-E12 forbids per-id loops) and `GetCollectedThisMonth`;
   Directory `GetOwnerLookup` (all owners incl. system, for the dashboard hero naming + roll-up id) and
   `GetDirectoryKpis` (vacancy / collectedTarget).

## Gotchas worth remembering

- **.NET 10 OpenAPI types `decimal` as JSON-Schema `["number","string"]`**, so the generated TS client
  widens every numeric field to `number | string`. The web uses a `num()` coercion at the render
  boundary. Any future frontend work hits this — keep `num()` in mind.
- **XSRF rotates on sign-in** — authenticated mutations must re-prime CSRF *after* login (the SPA's
  `primeCsrf` already does; the telemetry integration test had to learn it).
- **Lingering `dotnet run` hosts lock the build output** on Windows — kill stray `LeaseBook.Web`
  processes before `dotnet build` if you started a host for schema regen / smoke.

## What M3 planning must absorb

- **The `DepositApplied → AgainstCharges` over-clear guard the M3 composer owns** (m1_retro finding 1):
  the engine clamps the application to the held deposit but **not** to the open receivable, so the inline
  composer must clamp to the open receivable (PaymentReceived auto-splits excess to prepayments; this
  path does not). No M1 invariant breaks (I4 is liabilities-only), but the composer is the guard.
- **The directory detail/list shapes M3's ledger hub consumes:** `TenantDetail { balance, depositHeld,
  lease, unitLabel, propertyAddress, ownerName, status }` and `TenantListRow` are the contract the M3
  tenant header + ledger build on. The "Liability · not income" deposit framing is already rendered.
- **The action-provider seam M3 plugs "Record payment" into:** `features/palette/actionRegistry.ts`
  `registerActionProvider(...)`. M2 registered a `record-payment` action that *routes* to the tenant
  detail; M3 makes it open the composer (or post) without touching palette code.
- **The org basis preference the composer defaults to:** `org_settings.accounting_basis`, read on the
  web via `useOrgSettings()` (also drives the app-wide `MoneyDisplayProvider` negative-display). The M1
  read endpoints already default `basis` to this org preference.
- **Click-budget telemetry is wired but minimal:** `trackInteraction` → `POST /api/telemetry/budget` →
  OTel `ux.budget` span. M2 instruments `entity-jump` (≤ 2) and `owner-balances-visible` (0). M3's
  composer must instrument "record a tenant payment ≤ 3 interactions" (the budgeted UX contract). The
  dashboard-panel + release-gate wiring for these events is still a later cross-cutting task.

## Known limitations & follow-up hardening (from review of the M2 diff)

Three things passed the gate but are worth carrying forward — none is a correctness bug today; each is a
sharp edge a future change could cut on.

1. **Journal-dimension FKs guarantee existence, not org-correctness → plan a composite `(org_id, id)` FK.**
   ADR-008's five `journal_lines`→directory FKs are single-column. Postgres RI checks **always bypass
   RLS** (the very property the engine-test harness exploits), so the FK only proves a dimension id
   exists in *some* org, not the *same* org as the line. Safe at runtime today (directory ids are
   globally-unique PKs, generated in-org; RLS scopes reads), but the FK is **not** a cross-org isolation
   boundary and shouldn't be treated as one. **Fix (agreed direction):** `UNIQUE (org_id, id)` on the
   directory tables + composite `(org_id, <dim>_id) → (org_id, id)` FKs (`journal_lines` already has
   `org_id`). **Deferred on purpose, not done on `m2/directory`:** it forces a rework of
   `AccountingTestHarness` (its hidden-global-org FK-target trick stops working once the FK is org-aware —
   targets must seed into each test org) and a full gate re-run, so it would invalidate M2's green
   evidence right before the PR. **Do it in M3 or M4**, whichever first reopens `journal_lines` / the
   harness. Tracked in ADR-008 → Revisit trigger. If it needs to be a hard task rather than a note,
   promote it to a `private/TODO.md` line during M3 planning.
2. **`WHERE NOT is_system` is a review convention, not a global guard.** Golden tests assert the *current*
   roster/search/detail reads hide the aggregate rows, but nothing fails CI if a *new* directory read
   omits the filter and leaks "All other owners" / the synthetic deposit-aggregate tenants. Cheap
   hardening: a shared `NotSystem()` query helper every roster read funnels through, or a convention test
   over `Set<Owner>()`/`Set<Tenant>()` list queries. Until then it is a standing review checklist item on
   any new directory read. (Also noted in ADR-008 → Consequences.)
3. **Cosmetic, non-blocking:** web lint is *0 errors / 2 warnings* — `react-refresh/only-export-components`
   in `web/src/lib/recordNav.tsx` (split the shared constants/functions into a non-component module to
   clear it). Local dev env is Node 22 vs the repo's pinned Node 24; web build/test pass regardless, but
   match the pin before relying on lockfile-sensitive behavior.

## Still deferred (correctly out of M2)

Bank register / cleared-uncleared / reconciliation (M4 — `uncleared` shows 0 / "Reconciled"); owner
statements + PDF/CSV/email (M5 — owner/tenant detail are read-only); fee computation (M6 — M2 stores bps
only); migration toolkit (M7); user-management / invitations / roles (the identity soft-spot stays
untouched, P48); logo upload wiring (M5/M8 — `logo_blob_ref` stored only).
