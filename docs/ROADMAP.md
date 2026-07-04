# LeaseBook Roadmap — the consolidated engineering plan

- **Date:** 2026-07-04
- **Supersedes:** `docs/IMPLEMENTATION_PLAN.md` (PR #66, merged 2026-07-04 — its items A–H are
  absorbed below; mapping table in §2) and the maintainer's separate milestone plan for the same
  scope. This file is now the **single committed engineering roadmap**.
- **Authority note:** the canonical build plan is `private/TODO.md`, which is gitignored and
  absent from a public clone. This roadmap is its committed, engineering-only projection: where
  scope lives only in `private/` (product strategy, Phase-2 detail, anything commercial) that is
  said explicitly rather than reconstructed. When this file and the private plan disagree, the
  private plan wins and this file gets fixed. Per the repository's confidentiality rule, this file
  carries no pricing, strategy, or customer detail.
- **Structure:** three gating tracks. **Track A** is engineering-ready (buildable and verifiable
  locally, no Azure access). **Track B** is operator-gated (needs Azure / GitHub-org access;
  engineering supports). **Track C** is compliance/beta-gated (needs the external compliance
  review and the beta customer's data). Work is planned as work packages (**WP-1…WP-12**) whose
  numbering is stable — other documents reference these IDs.

---

## 1. Where the build stands (ground truth, verified 2026-07-04)

M0–M7 are complete and merged: foundations + RLS tenancy + CI (M0), the trust-accounting engine
and its invariant/property/golden harness (M1), Directory + ⌘K + dashboard (M2), the tenant-ledger
action hub (M3), Banking & reconciliation (M4), Owner statements & the report catalog (M5, PR #31,
ADR-016), Bulk operations (M6, PR #34, ADR-017/018/019), and the Migration toolkit + import-first
onboarding (M7, PR #36, ADR-020/021). M8 (hardening/beta) is the frontier and is partially shipped:

| Shipped M8 item                                                | Evidence                                                             |
| -------------------------------------------------------------- | -------------------------------------------------------------------- |
| `azure-infrastructure` specialist agent                        | PR #48; `.claude/agents/`                                            |
| CI e2e + automated a11y gate (light theme)                     | PR #54/#55; ADR-022; `web/e2e/a11y.spec.ts`; `ci.yml` `e2e` job      |
| Visual regression (7 gated states, Linux baselines)            | PR #59; ADR-023; `web/e2e-snapshots/`; `update-visual-baselines.yml` |
| Security: seeder production guard, CSV formula-injection guard | PR #37 (`SeederGuard`, `CsvFormulaGuard`)                            |
| Azure infra + deploy workflows (authored, not live)            | `infra/`, `deploy-dev.yml`, `deploy-prod.yml`                        |

**Verified-open positions this plan is built on** (all checkable in source):
`AllowedHosts: "*"` (`src/LeaseBook.Web/appsettings.json:8`) · cookie `SecurePolicy =
SameAsRequest` (`src/LeaseBook.Web/Auth/AuthServiceCollectionExtensions.cs:51,64`) · no
security-header/CSP middleware · no `RateLimiter` registration · TOTP MFA built but not enforced
for admins · no compliance-pack code · no load/perf fixture · no `infra/modules/network.bicep` ·
no Hangfire package (only the forward reference in `src/LeaseBook.Web/Cli/InvariantSweep.cs:13`) ·
no late-fee-policy settings UI in `web/src` · `CHANGELOG.md` `[Unreleased]` ends at M4 ·
CLAUDE.md "Repository state" still names M5 as the frontier · import re-import is figure-blind
(supersede path deferred, ADR-021 §"deferred to M8") · held-PM-fees opening positions are not
imported (ADR-020 §5 / ADR-021) · dark-theme a11y and visual coverage are tracked follow-ons
(ADR-022/023 Consequences).

**Closed since this baseline:** **WP-1** (merged, PR #68) fixed the CHANGELOG-ends-at-M4 and
CLAUDE.md-names-M5-frontier drift. **WP-2** guarded dark-theme a11y (the axe gate now scans both
themes; ADR-022 amended) — dark-theme _visual_ coverage remains open as WP-3.

---

## 2. Track map, sequencing, and the old→new mapping

### Track A — engineering-ready (recommended waves for a solo dev; ∥ = parallel-safe)

| Wave | WP    | Title                                   | Size | Depends on   |
| ---- | ----- | --------------------------------------- | ---- | ------------ |
| 1    | WP-1  | Public docs drift catch-up              | S    | — (do first) |
| 2    | WP-2  | Dark-theme a11y gate                    | M    | WP-1         |
| 2 ∥  | WP-4  | Extended e2e + keyboard/focus           | M    | WP-1         |
| 2 ∥  | WP-10 | Prod private networking Bicep + ADR-024 | M    | —            |
| 2 ∥  | WP-12 | GLBA data-handling docs draft           | S    | —            |
| 3    | WP-3  | Dark-theme visual variants              | S    | WP-2         |
| 3 ∥  | WP-5  | Security hardening pass                 | M    | WP-1         |
| 3 ∥  | WP-6  | Late-fee policy settings UI             | S–M  | WP-1         |
| 4    | WP-7  | Import supersede + held-PM-fees import  | M    | WP-1         |
| 4 ∥  | WP-8  | Trust Compliance Pack                   | M    | WP-1         |
| 5    | WP-9  | Performance fixture + p95 harness       | M    | WP-1         |
| 5 ∥  | WP-11 | Nightly invariant sweep (Hangfire)      | S–M  | WP-1         |

WP-8 and WP-12 produce inputs to Track C's compliance review — do not push them past wave 4.

**Specialist agents are mandatory per domain** (`.claude/agents/`): `react-frontend` (WP-2/3/4/6
UI), `dotnet-api` (WP-5/6/8/11), `trust-accounting` (WP-7 design gate; WP-8 read shapes),
`postgres-specialist` (WP-9 remediation, WP-11 grants), `azure-infrastructure` (WP-10, Track B),
`code-reviewer` before each merge, `docs-updater` per the Stop hook.

**Branching:** one branch/PR per WP, `m8/<wp-slug>` (matches `m8/ci-e2e-a11y-gate`,
`m8/visual-regression`).

### Track B — operator-gated (sequence: B1 → B2/B3/B4 → B5; B5 needs WP-10)

B1 dev-environment enablement · B2 first PITR restore drill · B3 ACS email + both send seams ·
B4 telemetry release gate + alerting · B5 prod leg.

### Track C — compliance/beta-gated

C1 NCREC-facing compliance review (ADR-014 names it; the two deferred fiduciary policies are
inputs) · C2 AppFolio real-column validation (`docs/migration/appfolio.md` §Gate) · C3 beta
cutover via the M7 toolkit.

### Mapping from `docs/IMPLEMENTATION_PLAN.md` (PR #66)

| Old item                                    | Now        |
| ------------------------------------------- | ---------- |
| A — Documentation drift catch-up            | WP-1       |
| B — Dark-theme a11y gate                    | WP-2       |
| C — Dark-theme visual regression            | WP-3       |
| D — Extended e2e coverage                   | WP-4       |
| E — Prod private networking + migration job | WP-10      |
| F — Live environment enablement             | B1 + B5    |
| G — First PITR restore drill                | B2         |
| H — Payments module outline                 | §6 Phase 2 |

Items with no old letter (WP-5..9, WP-11..12, B3, B4, Track C) come from the maintainer-side
reconciliation PR #66 explicitly requested — scope that was invisible without `private/` access
plus deferred items recorded across the ADRs (inventoried in §7).

---

## 3. Track A work packages

Every WP inherits the repository's non-negotiables (CLAUDE.md): money is `decimal`/`NUMERIC(14,2)`;
ledgers are append-only (corrections are linked reversals); PM income is structurally invisible to
owner-facing reads; RLS is the security boundary and new org-scoped tables go through the
migrations RLS helper; seed data is sacred — the demo golden figures move for no WP here; the 7
committed light-theme visual baselines are a tripwire (a light-pixel shift is a regression to fix,
never a re-baseline).

**Per-WP gate** (all green before PR): `dotnet build LeaseBook.slnx -c Debug` ·
`dotnet test LeaseBook.slnx` · `dotnet format --verify-no-changes --exclude
src/LeaseBook.Web/Migrations` · for web work `npm run lint && npm run typecheck && npm run test
&& npm run build && npm run format:check` · `npm run e2e` when e2e-adjacent ·
`check-invariants --org demo` exit 0 when accounting-adjacent.

---

### WP-1: Public docs drift catch-up (S) — `m8/docs-catch-up`

**Objective.** The committed docs describe an M4-era repo; every later PR's docs Stop hook fights
that stale baseline, so this lands first.

**Current state.** `CHANGELOG.md` `[Unreleased]` ends at Banking (M4) — nothing for M5/M6/M7/M8
and no `### Security` section; CLAUDE.md "Repository state" claims M5 is the frontier and calls
Reporting/Operations/Migrator shells; `src/LeaseBook.Web/Dashboard/DashboardService.cs:12` doc
comment still says "(TODO M2.4)" / "Reporting stays dormant".

**Steps.**

- [x] **1. CHANGELOG.** One `### Added` bullet block per milestone in the existing M0–M4 voice,
      sourced from the merged PRs/ADRs (never memory): M5 = statements + structural tie-out +
      fiduciary panel + report catalog + QuestPDF/CSV + delivery seam (PR #31, ADR-016); M6 =
      rent/late-fee/disbursement runs + run history (PR #34, ADR-017/018/019); M7 = migration
      toolkit + clearing tie-out + sign-off gate + onboarding wizard (PR #36, ADR-020/021); M8 =
      CI e2e + a11y gate (PR #54, ADR-022), visual regression (PR #59, ADR-023), authored Azure
      infra/workflows. New `### Security`: seeder production guard + CSV formula-injection guard
      (PR #37).
- [x] **2. CLAUDE.md "Repository state".** Rewrite: M0–M7 complete; M8 frontier with the shipped
      items from §1; `Payments` the only remaining shell; operator-gated remainder. Keep the
      "consult the live plan, not this summary" caveat verbatim.
- [x] **3. DashboardService comment.** Comment-only fix; then
      `dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations`.
- [x] **4. README sweep.** Done — the "What's implemented" table, the Status blockquote, the
      roadmap prose, and the architecture module tree were all M4-stale (statements/bulk-ops/
      migration listed as roadmap); brought current and linked to `docs/ROADMAP.md`. Port map
      unchanged (no port moved).
- [x] **5. Gate + commit.** `docs: catch public docs up to M5–M8 ground truth`.

---

### WP-2: Dark-theme a11y gate (M) — `m8/dark-theme-a11y` — closes ADR-022's follow-on

**Objective.** Extend the CI-gated axe scan (WCAG 2 AA) to the dark theme. ADR-022 Consequences:
"the axe scan covers the default (light) theme only; dark-theme accessibility is a tracked
follow-on, not yet guarded."

**Design.** The theme system already exists (`web/src/design/ThemeProvider.tsx` +
`web/src/design/tokens.css` dark custom properties) — what's missing is the scan. Parametrize the
existing spec by theme rather than duplicating it, so the route inventory can't drift between
themes.

**Steps.**

- [x] **1. Verify the theme contract (don't assume).** Confirmed in `ThemeProvider.tsx`: key
      `leasebook.theme`, shape `{ theme, accent, density }`, `readStored()` runs synchronously at
      first render so storage wins over `prefers-color-scheme`; `data-theme` stamped on `<html>`.
      The helper below was correct as written.
- [x] **2. Helper.** Add `seedTheme` to `web/e2e/helpers.ts` (WP-3 and WP-4 reuse it):

```ts
export async function seedTheme(
  page: Page,
  theme: "light" | "dark",
): Promise<void> {
  // Must run before any app script: ThemeProvider reads storage on boot.
  await page.addInitScript((t) => {
    localStorage.setItem("leasebook.theme", JSON.stringify({ theme: t }));
  }, theme);
}
```

- [x] **3. Parametrize `web/e2e/a11y.spec.ts`.** Wrapped both describe blocks in
      `for (const theme of ['light', 'dark'] as const)`, `seedTheme` before `signIn`, theme in the
      describe titles for unique names. Stayed in one file (sort-first ordering preserved); the
      `/operations` `nested-interactive` disable is identical in both branches; light branch
      behavior-identical.
- [x] **4. Run and fail.** First dark scan: 13 passed / 11 failed — all `color-contrast`, three
      root causes (muted `--text-3`; accent-emphasis text using the un-overridden light
      `--accent-strong`; white-on-vivid-accent buttons/avatars at 2.9:1).
- [x] **5. Fix.** Dark tokens (`--text-3` 0.57→0.64; new dark `--accent-fg` near-black so
      on-accent text is dark-on-vivid-teal per the maintainer's chosen direction; new dark
      `--accent-strong` via `color-mix` for accent-emphasis text). **Deviation from the plan's
      "dark tokens only, no per-component overrides":** two failures were pre-existing component
      bugs, not token contrast — `.pf-report-card` and `.pf-acct-tab` are `<button>`s with no
      `color`, so their text fell back to the UA default `#000` (invisible on dark). Fixed with a
      **dark-scoped** `html[data-theme='dark'] … { color: var(--text) }` per component, which
      leaves the light rendering (and its visual baselines) byte-identical. No silent excludes.
- [x] **6. Tripwire.** `npm run e2e -- a11y.spec.ts` → **24 passed** (both themes); lint (0
      errors), typecheck, 113 unit tests, and `format:check` all clean. Light visual baselines are
      untouched by construction — every change is dark-theme-scoped (dark token block +
      `html[data-theme='dark']` component rules); the CI visual gate confirms on the PR.
- [x] **7. Docs + commit.** ADR-022 Consequences amended (dark now guarded); CHANGELOG updated.
      `test(m8): extend the a11y gate to the dark theme (ADR-022 follow-on)`.

**Out of scope** (stated in the PR): the accent (teal/violet/green/navy) × density matrix — the
gate covers the default accent in both themes; the matrix is a possible future follow-up.

---

### WP-3: Dark-theme visual variants (S) — `m8/dark-visual` — closes ADR-023's deferral

**Objective.** Dark-theme snapshots for the theme-sensitive states. ADR-023 Consequences:
"dark-theme visual coverage is deferred (tracked with the dark-theme a11y follow-up)."

**Design.** Only snapshot states already passing WP-2's dark gate. Don't double all 7 — the three
where token regressions would actually show: `dashboard-full`, `ledger-composer-open`,
`owner-statement-full`.

**Steps.**

- [ ] **1.** Extend `visualSnapshot()` (`web/e2e/helpers.ts`) minimally — prefer explicit `-dark`
      snapshot names over a new option (zero API change).
- [ ] **2.** In `budgeted-flows.spec.ts`, `m3-ledger.spec.ts`, `m5-reports.spec.ts`: seed dark
      (WP-2's `seedTheme`) → drive to the same state → snapshot with the **same masks**
      (collected-this-month card, composer date field — the ADR-023 masking rules).
- [ ] **3. Baselines in one PR.** Use the bootstrap-from-CI-actuals pattern (commit `562a1c3`):
      push the spec, let CI render, commit the actuals as baselines — avoids the red-gate window
      of the post-merge `update-visual-baselines.yml` dispatch (which needs no change either way;
      it regenerates whatever `toHaveScreenshot` calls exist).
- [ ] **4.** Verify a dispatch re-run is byte-stable within the 2% tolerance. Amend ADR-023;
      CHANGELOG. `test(m8): dark-theme visual baselines for the theme-sensitive states`.

---

### WP-4: Extended e2e — Directory nav, error states, keyboard-only (M) — `m8/extended-e2e`

**Objective.** Close ADR-022's deferred coverage list (Directory navigation depth, error-state
rendering, keyboard-only sequences) plus the keyboard/focus items of the hardening checklist.
Test-only except small focus/tabIndex fallout fixes.

**Steps.**

- [ ] **1. `web/e2e/directory-navigation.spec.ts`.** Owners list row-click → owner detail;
      properties list → property detail (aggregating units/tenants/owner); tenants list → ledger;
      units via property detail; ⌘K jump to one entity of each type; next/previous record
      quick-switcher; back/breadcrumb integrity. (Current specs only exercise ⌘K → tenant.)
- [ ] **2. `web/e2e/error-states.spec.ts`.** Add `routeFail(page, urlPattern)` to `helpers.ts`
      (`page.route(pattern, r => r.fulfill({ status: 500, … }))`). Assert: 500 on a list query →
      the designed error state (not blank/raw text); 500 on a ledger post → composer error
      surface, no phantom row; and that **no client-side financial math** fills a failed server
      figure (the SPA renders server figures only). Scope every interception to its test so it
      never leaks into other specs.
- [ ] **3. Empty states.** Evaluate before building: the cutover org is already
      banks-and-CoA-with-no-journal-data but redirects to `/onboarding` — decide per screen
      whether post-sign-off cutover covers the empty render or a true `--org empty` seeder
      variant is needed. If needed: additive, `SeederGuard.RequireNonProduction`-guarded, zero
      effect on demo goldens; wire into the CI `e2e` job's seed step alongside demo+cutover.
- [ ] **4. `web/e2e/keyboard-only.spec.ts`.** One budgeted flow keyboard-only end-to-end
      (⌘K → tenant → composer → amount → Enter posts; reuse the interaction-counting approach
      from `budgeted-flows.spec.ts`; budget still ≤ 3). Reconcile mode Select All → Finalize by
      keyboard. Palette arrows + Enter. Focus assertions: focus returns to trigger on
      drawer/modal close; composer autofocus fires; `:focus-visible` ring present (the PR #54
      focus-ring rekey showed this area is fragile). Settings: bank-account deactivate toggle +
      guard path (closes a known e2e gap).
- [ ] **5. Fallout fixes.** Expect small `tabIndex`/focus-order fixes in `web/src/` —
      design-token classes only. If a budgeted flow's implementation changes, its existing e2e
      must still pass (the UX-contract invariant).
- [ ] **6. Gate + docs.** Full `npm run e2e`; watch total job runtime (ADR-022 flags e2e as the
      slowest job) — keep specs lean and serial-friendly; if runtime becomes the constraint,
      propose sharding in a future spec, not here. Amend ADR-022's deferred list; CHANGELOG.
      Commits per spec: `test(m8): directory navigation e2e` / `test(m8): error-state e2e` /
      `test(m8): keyboard-only operability e2e + focus fixes`.

---

### WP-5: Security hardening pass (M) — `m8/security-hardening`

**Objective.** Close the host-level hardening items visible in source: `AllowedHosts` pin,
security headers + CSP, MFA enforcement for admins, cookie `Secure` policy, auth rate limiting,
and an authz matrix test. (Everything here is checkable against public source — see §1.)

**Steps.**

- [ ] **1. AllowedHosts.** Keep `*` for Development; add `appsettings.Production.json` whose
      `AllowedHosts` is supplied by deploy-time configuration (document the exact key in
      `infra/README.md`'s configuration contract so Track B sets the real hostname). Integration
      test: host filtering rejects a forged `Host:` header when configured.
- [ ] **2. Security headers + CSP.** Middleware (`src/LeaseBook.Web/Security/`) adding on every
      response: `X-Content-Type-Options: nosniff`, a strict `Referrer-Policy`, a restrictive
      `Permissions-Policy`, and a CSP for the self-hosted SPA starting from
      `default-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'`.
      **Verify before hardening:** run the built SPA under the
      enforced policy locally and tighten to what it actually needs (Vite output may require
      `style-src 'unsafe-inline'`) — don't guess. Ship enforced (no deployed env exists to break;
      fixing violations now is cheapest). Integration test asserts each header on `/` and an
      `/api` response.
- [ ] **3. MFA enforcement for `PMAdmin`.** Authorization policy: `PMAdmin` principals must have
      completed TOTP enrollment (`TwoFactorEnabled`), enforced on the authenticated endpoint
      groups with a carve-out for the enrollment endpoints themselves. Default: hard block at
      next login (a beta org can enroll its one admin before merge day); a grace window is the
      alternative if the operator prefers. Tests: un-enrolled `PMAdmin` → 403 + problem-details
      pointing at enrollment; enrolled → 200; `PMStaff` unaffected.
- [ ] **4. Cookie `Secure` policy.** At `Auth/AuthServiceCollectionExtensions.cs:51,64`, make the
      policy environment-driven: `SameAsRequest` in Development (local :5080 is http),
      `CookieSecurePolicy.Always` otherwise. Read the class first to see how the environment
      reaches it (it may need `IWebHostEnvironment` threaded in or a check at the Program.cs
      call site).
- [ ] **5. Auth rate limiting.** `AddRateLimiter` with a fixed-window per-IP policy applied via
      `.RequireRateLimiting("auth")` to the auth endpoint group only (login, MFA challenge;
      e.g. 10/min → 429 + Retry-After); `app.UseRateLimiter()` in the pipeline. Size the window
      generously above the e2e suite's serial login rate so CI isn't tripped. Integration test:
      11th in-window attempt → 429.
- [ ] **6. Authz matrix test.** Walk the endpoint data source and assert every mapped endpoint
      carries an authorization policy (deny-by-default; no anonymous surface outside the explicit
      auth + health allow-list), plus role-matrix probes on representative money endpoints
      (posting, disbursement run, period unlock): anonymous → 401, barred `PMStaff` → 403,
      `PMAdmin` → 2xx.
- [ ] **7. Gate + docs.** Full backend suite + e2e (login flow touches cookies + limiter).
      CHANGELOG `### Security`. Commit:
      `feat(m8): security hardening — headers/CSP, AllowedHosts, MFA enforcement, auth rate limit, authz matrix`.

---

### WP-6: Late-fee policy settings UI (S–M) — `m8/late-fee-settings`

**Objective.** The `late_fee_policies` table and the late-fee run engine shipped with M6
(PR #34; NC §42-46 clamp in `LateFeeRunStrategy`), but no settings surface edits
`grace_days` / `flat_fee` / `rate_bps` / `cap_amount`. Close the gap.

**Steps.**

- [ ] **1. Verify the surface (nothing here is guessed-safe).** Read the entity/table in
      `src/LeaseBook.Modules.Operations/` and `LateFeeRunStrategy`: confirm the field set,
      flat-vs-rate semantics, the statutory clamp behavior, whether policy rows are per-org
      singletons or per-property, and whether any read slice already exists (the run engine is
      served by `ILateFeePolicyData` — the settings surface may only need the write side).
- [ ] **2. TDD the Operations slice.** `GetLateFeePolicy` query + `UpdateLateFeePolicy` command
      (record + `AbstractValidator` + handler, one file each). Failing tests first: negative
      fees rejected; bps out of range rejected; grace-days bounds; the clamp stays engine-owned
      (don't duplicate it in the validator); the update writes an audit event (a money-policy
      change is audit-worthy). Then the minimal handler; green.
- [ ] **3. Endpoints + client.** `GET/PUT /api/operations/late-fee-policy` (match the existing
      settings grouping; deny-by-default, `PMAdmin` on the PUT). `npm run api:generate`
      (ADR-012 drift gate).
- [ ] **4. UI.** Settings-page section: current policy + edit form; Money inputs render tabular
      numerals; bps shown as %, stored as bps (the M2 fee-config convention). Empty/loading/error
      states; keyboard path.
- [ ] **5. e2e.** Edit policy → late-fee run **preview** reflects the new grace/fee — preview
      only, never post (demo goldens are sacred); `check-invariants --org demo` exit 0 after the
      spec.
- [ ] **6. Gate + docs.** CHANGELOG. Commit: `feat(m8): late-fee policy settings UI`.

---

### WP-7: Import correction/supersede + held-PM-fees opening import (M) — `m8/import-closeouts`

**⚠️ trust-accounting design gate:** invoke the `trust-accounting` agent and write a short design
note (`docs/superpowers/specs/`) **before any code**. Both halves touch posting semantics.
Load-bearing inputs: ADR-020 (esp. §5's held-fees line table and the explicit exclusion note),
ADR-021 (§"no supersede/correction workflow — deferred to M8"), `BalanceImportService`,
`VerificationService`, and `docs/accounting.md`'s migration section.

**Half A — pre-sign-off supersede.** Today re-import is figure-blind idempotency: the opening
entry's `source_ref` (`opening:{cutover}:{type}={subledgerId}`) identifies the position, not the
amount, so a corrected figure never posts and fixing one means re-provisioning the org. Semantics
to encode:

- Pre-sign-off **only** — after sign-off the org is live and corrections are ordinary linked
  reversals through the ledger, not import machinery. Guard: HTTP 409 on a supersede attempt for
  a signed-off org.
- Superseding batch B with B′: for each row whose figure changed, post a **linked reversing
  entry** of the original opening entry (append-only — never update/delete), then post the
  corrected position under a `source_ref` revision scheme the design note must pick (e.g. a
  `#rev` suffix) without weakening the duplicate-detection backstop.
- Mark the old batch `import_batches.status = superseded` (schema-defined since M7, unexercised);
  unchanged rows are not re-posted.
- Verification must re-run after supersede (sign-off already re-derives the tie-out); the
  clearing account must net to $0.00 in **both** bases afterward.
- Audit events on supersede; the onboarding wizard surfaces a "corrected re-import" path on the
  balance step.

**Half B — held-PM-fees opening import.** New balance kind (CSV profile + typed row + binder in
`LeaseBook.Migrator`, following the existing per-kind pattern) posting the ADR-020 §5 held-fees
opening position. It touches `pm_income`-adjacent accounts — exactly why M7 excluded it — so the
design note must show the exact per-basis lines from ADR-020 §5 and prove no owner-statement
reachability. Today a held-fee position surfaces only as a clearing residual the operator
reconciles manually before sign-off.

**Steps.**

- [ ] **1.** Design note covering both halves (trust-accounting agent); review before code.
- [ ] **2.** TDD Half A: engine tests first (supersede posts reversal+corrected pair; clearing $0
      both bases; signed-off org → 409 with no side effect; unchanged rows untouched), then
      endpoint + wizard surface, then extend `web/e2e/m7-onboarding.spec.ts` with the correction
      path (import wrong figure → supersede → verify ✓ → sign off).
- [ ] **3.** TDD Half B: profile/binder unit tests (Migrator suite, no Testcontainers); posting
      test asserting per-basis lines match the design note; owner-statement-reachability test
      (by construction + a query-result assertion, the invariant-#3 style); tie-out including
      held fees.
- [ ] **4.** Gates incl. `check-invariants --org cutover` after a full wizard run; demo golden
      untouched; property suite green. ADR-020/021 get amendments (not new ADRs — this executes
      their documented deferrals). Commits: `feat(m8): pre-sign-off import supersede workflow` /
      `feat(m8): held-PM-fees opening-balance import (ADR-020 §5)`.

---

### WP-8: Trust Compliance Pack (M) — `m8/compliance-pack`

**Objective.** One click → an audit-shaped bundle for the NCREC trust-recordkeeping context the
product is built around: trust account ledger, reconciliation history (the stored immutable M4
reports), security-deposit liability register, and an audit-log extract. Everything it needs
already exists in M4/M5 — this composes, it doesn't compute.

**Steps.**

- [ ] **1. Verify the sources.** Confirm each artifact is producible for an arbitrary
      period × trust account from existing slices: trust ledger + deposit register (M5 report
      catalog), reconciliation snapshots (`IReconciliationSnapshots`), audit extract
      (`audit_events` — `PMAdmin`-scoped, filtered to period + money-touching actions; this is
      also the first brick of audit-log review tooling). List any gap before coding. Boundary:
      the pack lives in Reporting under the ADR-016 read-layer sanction.
- [ ] **2. Format (recommend + proceed).** ZIP of per-report PDFs + a CSV manifest, fronted by a
      QuestPDF cover/index page (org branding, period, contents, generation stamp) — auditors
      want discrete documents, not a mega-PDF. State in the PR; no new ADR (composition, not a
      new decision).
- [ ] **3. TDD.** Assembler test on the demo org: the pack contains exactly the four artifacts
      for the period; cover totals tie to the ledger-report totals; **no `PMIncome`-derived
      figure appears in any owner-facing artifact** (the management-fee income report is
      deliberately NOT in the pack — it is PM-facing). Audit event emitted on generation (a
      compliance export is itself audit-worthy). Then endpoint
      (`GET /api/reports/compliance-pack`), catalog UI entry with period + trust-account filter
      chips, and an e2e download check.
- [ ] **4. Gate + docs.** CHANGELOG. Commit: `feat(m8): one-click trust compliance pack`.

---

### WP-9: Performance — 300-unit fixture + p95 harness (M) — `m8/perf-harness`

**Objective.** Prove the p95 < 300 ms budget on ledger / dashboard / register reads at the
anticipated design scale (~300 units / ~23 owners — the scale ADR-016 names). No load fixture
exists today.

**Steps.**

- [ ] **1. Fixture.** `seed --org load` (`LoadSeeder` alongside `DemoSeeder`/`CutoverSeeder`):
      ~300 units across ~40 properties / ~25 owners with ~12 months of activity generated
      **through the existing posting templates and bulk-run engine** (rent runs, payments, fees,
      disbursements, reconciliations) — never raw journal inserts. Deterministic RNG seed;
      `SeederGuard.RequireNonProduction`; idempotent like the demo seeder; keep seed wall-time
      tolerable (target < ~2 min — it's a dev-only fixture). `check-invariants --org load` must
      exit 0: a load fixture that violates the trust equation is a bug in the fixture (or a real
      engine find — either way it fails loudly).
- [ ] **2. Harness.** Simplest honest tool: a CLI verb (`src/LeaseBook.Web/Cli/PerfProbe.cs`)
      that, against the running host + load org, hits the three read paths N=100 each after
      warmup (tenant ledger page, dashboard KPIs + hero, bank register page), reports
      p50/p95/p99, and exits non-zero if p95 ≥ 300 ms. Deliberately **not** a CI gate initially
      (local hardware variance) — a documented, repeatable local check; revisit CI-gating once a
      deployed environment exists (Track B).
- [ ] **3. Remediate.** If p95 misses: `EXPLAIN (ANALYZE, BUFFERS)` the offender and fix with the
      `postgres-specialist` agent (likely: a covering index leading with `org_id`, or a
      window-function rewrite) — never a denormalized cache of ledger state (read models stay
      projections of the journal; materialization would be the ADR-016 revisit, its own ADR).
- [ ] **4. Gate + docs.** `docs/perf.md` records method + first measured numbers + date;
      CHANGELOG. Commit: `test(m8): 300-unit load fixture + p95 latency harness`.

---

### WP-10: Prod private networking Bicep + migration-as-Container-Apps-Job — ADR-024 (M) — `m8/prod-networking`

**Objective.** Author the missing VNet layer so a prod deploy can reach the
`publicNetworkAccess: 'Disabled'` PostgreSQL Flexible Server (`infra/README.md` §Production
networking leaves this open), and move prod migrations off GitHub-hosted runners — which cannot
reach a private-only DB. Execute with the `azure-infrastructure` agent; authoring +
`az bicep build` are engineering-ready, `what-if`/apply is Track B.

**Steps.**

- [ ] **1.** `infra/modules/network.bicep`: VNet + delegated PG subnet
      (`Microsoft.DBforPostgreSQL/flexibleServers`) + ACA infrastructure subnet +
      `privatelink.postgres.database.azure.com` private DNS zone + VNet link. Verify current
      subnet-sizing minimums against live Azure docs at authoring time (ACA workload profiles
      historically wanted /23).
- [ ] **2.** `infra/main.bicep`: `enablePrivateNetworking` param (dev `false` — zero behavior
      change; prod `true`); thread `delegatedSubnetResourceId` + `privateDnsZoneArmResourceId`
      into `database.bicep` and `vnetConfiguration.infrastructureSubnetId` into
      `containerapp.bicep`.
- [ ] **3.** `containerapp.bicep`: add the migrator **Container Apps Job** (the existing
      `migrator` Dockerfile target — confirm the target name in the root `Dockerfile` first).
- [ ] **4.** `deploy-prod.yml`: replace the runner-side `dotnet ef database update` with
      build/push of the migrator image + `az containerapp job start` + wait-for-completion.
- [ ] **5.** `docs/adr/ADR-024-prod-private-networking-and-migration-job.md` (+ README index
      row): decision = ACA Job in-VNet; rejected = self-hosted runner in the VNet, temporary
      firewall exceptions.
- [ ] **6.** `infra/env/prod.bicepparam` address spaces (**operator confirms CIDR ranges**;
      default: propose non-overlapping ranges in the ADR); rewrite `infra/README.md` §Production
      networking to the implemented design.
- [ ] **7. Gate.** `az bicep build --file infra/main.bicep` clean (no credentials needed).
      Commit: `infra(m8): prod private networking + migrator ACA job (ADR-024)`.

---

### WP-11: Nightly invariant sweep via Hangfire (S–M) — `m8/nightly-sweep` — first ADR-001 integration

**Objective.** The sweep body exists (`InvariantSweep.RunAsync` — per-org `OrgScopedExecutor` +
`IInvariantChecks.CheckCoreAsync`; its own doc comment names this WP). Give it the scheduler
ADR-001 chose (Hangfire + Hangfire.PostgreSql) and emit the telemetry event Track B alerts on.
The page-on-variance **alert** half stays Track B (needs App Insights).

**Steps.**

- [ ] **1. Grants design first (`postgres-specialist`).** Hangfire.PostgreSql wants its own
      schema (`hangfire`) with full DML for the runtime role. These tables are **global-class**
      (no `org_id`, no RLS — verify the schema-guard test keys on `org_id` columns and won't
      false-positive). Extend `infra/db/bootstrap.sql` + the local compose bootstrap so
      `leasebook_app` owns/uses the `hangfire` schema **without** touching the journal
      append-only revocations. Decide + document whether Hangfire's automatic migrations run
      under app or migrator role (prefer: schema pre-created by bootstrap; Hangfire objects
      app-owned — they're queue state, not domain schema).
- [ ] **2. Packages + wiring.** `Hangfire.AspNetCore` + `Hangfire.PostgreSql` in
      `LeaseBook.Web.csproj` only (host concern, not a module). `AddHangfire` +
      `AddHangfireServer` gated behind config (`Jobs:Enabled`, default false in Development so
      local dev and the e2e host stay deterministic).
- [ ] **3. The job.** Refactor `InvariantSweep` so the CLI verb and the job share one core
      (`ISweepRunner`): enumerate orgs via the narrow system path → per-org scoped run →
      structured log + telemetry event per violation (`trust.invariant.violation` — the alert
      key). Recurring job `invariant-sweep`, cron `0 7 * * *` UTC. The wrapper throws on missing
      org context (never silently-empty RLS reads).
- [ ] **4. Dashboard: none.** Do not mount the Hangfire dashboard in Phase 1 (attack surface; a
      solo operator reads logs). Note the deliberate omission in the PR.
- [ ] **5. Tests.** Integration: with `Jobs:Enabled=true` the recurring job is registered;
      executing the shared core against the seeded orgs → zero violations; a deliberately
      corrupted org fixture (in-test transaction, rolled back) → violation event emitted.
- [ ] **6. Gate + docs.** CHANGELOG; one-line ADR-001 amendment ("first integration landed").
      Commit: `feat(m8): nightly trust-invariant sweep (Hangfire, ADR-001)`.

---

### WP-12: GLBA data-handling docs draft (S) — `m8/glba-docs`

**Objective.** The compliance posture (CLAUDE.md lists GLBA among the standing gates) exists in
code and infra but not as documents. Engineering drafts; external review finalizes wording
(Track C). No code.

**Steps.**

- [ ] **1.** `docs/compliance/data-handling.md`: data inventory (what PII/financial data lives
      where — Postgres tables by class, Blob artifacts, App Insights telemetry), encryption
      posture (TLS in transit, Azure encryption at rest, Key Vault), access model (three DB
      roles, RBAC, RLS), retention windows per data class, and the offboarding story (per-org
      export + documented hard-delete path).
- [ ] **2.** `docs/compliance/privacy-notice-draft.md`: GLBA-shaped customer-facing notice
      skeleton with explicit `[LEGAL REVIEW]` markers on every legal-assertion sentence.
- [ ] **3.** CHANGELOG. Commit: `docs(m8): GLBA data-handling documentation + privacy notice draft`.

---

## 4. Track B — operator-gated go-live ([OP] = operator access, [ENG] = engineering support)

### B1 — Dev environment enablement

- [ ] [OP] Entra app registration + OIDC federated credentials for the `dev` GitHub environment;
      secrets `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID`; vars per
      `infra/README.md` naming (`lbdevacr`, `lb-dev-app`, `lb-dev-rg`).
- [ ] [OP] `az deployment sub create … infra/env/dev.bicepparam`; bootstrap the three Postgres
      roles per `infra/db/azure-bootstrap.md`; Key Vault secrets per the README contract; the
      workflow's `MIGRATIONS_CONNECTION_STRING`; the `AllowedHosts` configuration value (WP-5).
- [ ] [OP] Run `deploy-dev.yml` → image push, migration step, Container App revision,
      `/api/health` 200.
- [ ] [ENG] First-deploy fix PR if anything surfaces — fixes land in `infra/`/`Dockerfile`,
      never as portal-only drift. Optionally seed demo in dev (the seeder refuses Production —
      prod is never seeded).

### B2 — First PITR restore drill (after B1)

- [ ] [ENG] Verify + document `check-invariants --all` against an arbitrary DB via a
      `ConnectionStrings__Default` override in `docs/runbooks/restore.md` step 3.
- [ ] [OP] Execute: restore to `lb-dev-pg-restored` → invariant suite green against the restored
      DB (a restore that doesn't reconcile to the cent is not a successful restore) → practice
      the Key Vault repoint → revert → decommission. Fill the runbook's `TODO (first drill)`
      (duration, data-loss window, surprises); drop the "skeleton" caveat. Dev retention is
      7 days — drill within a week of meaningful data.

### B3 — ACS email + both send seams (any time after B1)

- [ ] [OP] Provision Azure Communication Services + verified sender domain; connection secret
      into Key Vault per the secrets contract.
- [ ] [ENG] ACS implementation of `IStatementDelivery` (replacing `LocalStatementDelivery`
      outside dev): delivery states queued/sent/failed persisted on `StatementDeliveryRecord`;
      the sent artifact stored immutably via `IArtifactStore` (the stored copy is what the owner
      actually received). Then the owner-disbursement notification on the same sender.
      Integration tests against a fake sender; one manual [OP] send verification in dev.

### B4 — Telemetry release gate + alerting

- [ ] [OP] App Insights live (with B1); a workbook panel for the four click-budget metrics from
      `trackInteraction` events (payment ≤ 3 interactions · owner balances 0 clicks · uncleared
      ≤ 1 click · reconcile start ≤ 2 clicks). The Playwright budget assertions remain the local
      enforcement; this adds the release-time view.
- [ ] [ENG] Release-gate query/script: a budget regression fails the release checklist; document
      in the release runbook.
- [ ] [OP] Alert rules: `trust.invariant.violation` (WP-11) → immediate page; failed-job /
      error-spike / uptime checks; a 2 a.m. on-call checklist in `docs/runbooks/`.

### B5 — Prod leg (after WP-10 + B1–B4 stable)

- [ ] [OP] `prod` GitHub environment + required reviewers; deploy `prod.bicepparam` (private
      networking on); roles bootstrapped via a VNet-reachable path per ADR-024; promote a
      dev-built image via `deploy-prod.yml`; verify the migrator ACA-Job path end-to-end;
      confirm HTTPS-only ingress (pairs with WP-5's `CookieSecurePolicy.Always`).

---

## 5. Track C — compliance review & beta cutover

- [ ] **C1 — External trust-accounting compliance review** (the NCREC-facing review ADR-014
      anticipates; operator schedules — longest lead time in the remainder, start early).
      Engineering-visible inputs to the review packet: `docs/accounting.md`,
      ADR-006/011/014/016/020, the WP-8 Compliance Pack output on the demo org, the WP-12
      drafts, and **ADR-014's two deliberately deferred fiduciary policies** — (a) interest
      entitlement on trust funds (M4 credits the PM's held position by default) and (b)
      trust-fee coverage being procedural rather than structural. The review replaces those
      deferred defaults with explicit policy; each change lands as an ADR-014 amendment or a new
      ADR + posting-template change with invariant tests.
- [ ] **C2 — AppFolio real-column validation** (`docs/migration/appfolio.md` §Gate; clears only
      with the beta customer's real export files): validate/update the `AppFolioProfiles` header
      candidates, drop the best-guess caveats, run the full import on a staging org, and confirm
      single-vs-multi trust accounts — if multiple, the `owner_balances` routing disambiguation
      (WP-7's design note; today `ResolveOperatingTrustAsync` picks the oldest Trust-purpose
      account) gets built before cutover.
- [ ] **C3 — Beta cutover** via the M7 toolkit: staging import → $0.00 verification →
      sign-off → go-live; parallel-run per `docs/migration/parallel-run.md`; statements verified
      against the customer's own reconciliation with zero manual adjustments (the structural
      tie-out made real); the UX-contract budgets measured from real usage (B4). Remaining
      acceptance detail is tracked in the private plan.

---

## 6. Phase 2 — Payments (outline only; scope authority is private)

`src/LeaseBook.Modules.Payments/` stays a compile-time shell until Phase-2 scope is supplied from
the private PRD — do not design against a guess. The committed, non-negotiable constraints that
will bind any design:

- Module boundary (ADR-007): Payments reads other modules only through consumer-owned batch ports
  in its `Contracts/`, host adapters delegating via `ISender`.
- All money movement posts through **posting templates** (ADR-006) on the append-only journal — a
  processor webhook never writes ledger state directly; it raises business events
  (`PaymentInitiated` → `PaymentSettled` / `PaymentFailed` / `PaymentReturned`) whose templates
  post per basis. NSF/chargeback = linked reversal + fee event, never an update.
- New tables (payment intents, processor refs, webhook inbox) are org-scoped through the RLS
  helper; webhook processing is a background path → explicit org context, fail closed; idempotent
  webhook inbox (unique processor event id) — processors redeliver.
- Money stays `decimal`/`NUMERIC(14,2)`; processor minor-unit amounts convert at the edge with an
  exactness check.
- Processor fees charged to the PM are PM-expense postings, never owner-ledger reachable;
  deposits collected online remain liabilities until applied (ADR-011).
- Expected ADRs: processor selection + webhook/idempotency model; settlement/clearing account
  design (an undeposited-funds clearing account mirroring the M7 migration-clearing
  nets-to-zero pattern). Stripe payout lines reconcile through the existing
  `IBankRegister`/`AutoMatcher` path (ADR-015 revisit note).

**Rough shape once unblocked:** ADR + spec (S) → schema/migrations + posting templates +
invariant tests (M) → webhook inbox + processor adapter behind an `IPaymentProcessor` seam with a
fake for tests (M) → SPA surfaces + e2e (M). **Size: L overall.**

---

## 7. Deferred backlog & standing revisit triggers (inventoried from the ADRs)

Not scheduled work — each item names the event that promotes it into a WP. Kept here so deferrals
stay deliberate instead of forgotten.

| Deferred item                                                         | Where recorded                       | Fires when                                                                                                     |
| --------------------------------------------------------------------- | ------------------------------------ | -------------------------------------------------------------------------------------------------------------- |
| OFX/QFX statement import (behind `IStatementParser`)                  | ADR-015                              | a bank can't produce workable CSV / import is scheduled                                                        |
| Statement read-model materialization (Approach B)                     | ADR-016                              | statement generation measurably slows page loads at ~300-unit scale (WP-9 will produce the first real numbers) |
| Trust-interest entitlement + trust-fee coverage policy                | ADR-014                              | Track C1 review (this roadmap schedules it)                                                                    |
| Deposit-disposition wizard deriving refund bank from liability source | ADR-014 consequences / Phase-3 scope | Phase 3 move-out work                                                                                          |
| Multi-source import (Buildium/Rentec profile registry)                | ADR-020/021                          | a second PM-software source is onboarded                                                                       |
| Redis (cache/sessions)                                                | ADR-002                              | Phase-2 scale/session need materializes                                                                        |
| Per-persona RLS for portals (vs app-layer scoping)                    | ADR-003                              | portal endpoint surface grows past a handful (Phase 2–3)                                                       |
| Percy/Chromatic/Applitools cross-browser diffing                      | ADR-023                              | visual flake beyond the 2% tolerance                                                                           |
| Accent × density a11y matrix                                          | WP-2 out-of-scope note               | a non-default accent ships as a supported default                                                              |
| e2e job sharding                                                      | ADR-022 runtime note                 | e2e runtime becomes the CI constraint                                                                          |
| TypeScript 6 upgrade                                                  | `ts6-unblock-watch.yml`              | automated — the watcher files an issue when `openapi-typescript` unblocks                                      |

---

## 8. Explicit non-items

- `JournalLineConfiguration.cs:58`'s "§1 of TODO" comment is a citation, not an open task.
- The Hangfire dashboard stays unmounted in Phase 1 (WP-11 step 4 — deliberate).
- No staging environment: the model is dev + prod (staging deferred; revisit at Phase 2 scale).

---

## 9. Keeping this document honest

- Each WP's PR ticks its own checkboxes here and updates §1's evidence table in the same change —
  the repo's docs Stop hook flags drift otherwise.
- Scope changes are edits to the private plan first; this file follows. If a WP is dropped or
  re-cut, record why in the PR that edits this file.
- ADR-per-deviation still applies: WP-10 records ADR-024; WP-7 amends ADR-020/021; WP-2/3 amend
  ADR-022/023 Consequences; Track C1 outcomes amend ADR-014.
