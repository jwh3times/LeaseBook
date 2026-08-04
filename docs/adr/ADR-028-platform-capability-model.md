# ADR-028: Platform capability model — feature flags, entitlements, and the money rule

- **Status:** Accepted
- **Date:** 2026-08-03
- **Deciders:** Engineering

## Context

Two different questions were about to be answered by one mechanism:

- **"Is this behavior turned on for this deployment?"** — an operations question. The answer is
  temporary, changes during incidents, and applies to everyone at once.
- **"Is this organization allowed to have it?"** — a commercial question. The answer is durable, is
  per-organization, and is the sort of fact a customer can be told.

A single on/off store answers both, badly. The concrete failure it produces: an operator flips a kill
switch during an incident, and every organization that was _paying_ for that behavior is silently
downgraded — with a state store that cannot distinguish "we turned this off for everyone" from "this
organization was never entitled to it", the audit trail cannot answer which happened. Restoring the
flag afterwards restores the behavior and leaves no record that entitled organizations lost it.

Two constraints shaped the rest. Production has no public database endpoint and no administrative
UI (ADR-027), so whatever surface writes this state has to work from inside the network with no
browser. And LeaseBook's ledgers are fiduciary: a capability that can influence what a posting run
produces is a materially different object from one that hides a menu item.

## Decision

**Capabilities are resolved from two independent sources behind one seam, over a catalog defined in
source code, and they may gate reachability only — never money.**

### 1. Two sources, one seam

`ICapabilityGate` answers one question. Behind it sit three tables — `feature_flags` (deployment-wide
ops toggles), `entitlements` (per-organization grants), and `capability_cohorts` (per-organization or
per-user beta membership) — plus the registry default. No feature module reads those tables; the
host's `capabilities` CLI verb and its startup registry validator do, through the shared
`AppDbContext` (§15 Q2).

### 2. Resolution order

Evaluated in this order, for each capability:

1. `RequiresGrant && !HasGrant` → **false**. The entitlement gates first, so an operations rollout
   can never hand out a paid capability.
2. `FlagEnabled == false` → **false**. An explicit kill beats a cohort.
3. `CohortMatch` → **true**.
4. Otherwise `FlagEnabled ?? DefaultEnabled`.

The distinction the whole order turns on: **"off by default" is _no row_ plus `DefaultEnabled: false`,
whereas "killed" is an _explicit_ `enabled = false` row.** Step 2 is `== false`, not `!= true`,
precisely so an absent row stays available to step 3. Cohorts therefore OR onto an _absent_ flag —
which is how a beta is run, and why running one does not weaken the kill switch. Had step 3 come
first, flipping a money-path kill switch during an incident would have left the capability live for
exactly the cohort most likely to be exercising it.

Where there is no authenticated user — a background job, the CLI, the nightly sweep — user-level
cohort rows evaluate to no match rather than to an error; organization-level rows still apply.

### 3. The registry is source code, never rows

`Capabilities.All` defines what exists; the database stores only state. A capability that could be
_defined_ by a row is not merely untidy — it cannot be guarded. Every CI gate here works by
enumerating the catalog, and a gate that enumerated `feature_flags` instead would enumerate the empty
Testcontainers database, find nothing to check, and pass vacuously forever.

The consequences are load-bearing: the CLI can reject an unregistered name at the moment of the typo,
`IsMoneyPath` is a property of the code that reads a capability rather than of an operator's memory,
and a row naming an unknown capability is inert. Because it is inert, the startup validator logs
drift and continues in Production rather than refusing to boot — a hard fail there would make the
previous revision, the one an operator is rolling back to, the exact revision that cannot start.

### 4. The money rule: capabilities gate reachability only

A capability may decide **whether a posting path runs at all** — an endpoint, a command, a run-strategy
selection. It may **never** change the lines or amounts an existing business event produces. No value
read off a capability set may become an argument to an Accounting command, a business event, or a
posting-template input.

Money-affecting _parameters_ belong in `OrgSettings`: organization-scoped, RLS-enforced, audited
through `IOrgScoped`, seeded, and golden-pinned. Capabilities have none of those properties, and
adding them would make capabilities a second, weaker settings store on the fiduciary path.

Four architecture tests enforce what a reference graph can see: `Accounting` may not reference the
Capabilities module; no capability-shaped type may be declared in `SharedKernel` (which every module
references); **no capability-shaped type may be declared in `Accounting` either**, which closes the
locally-cloned-type route a reference-graph reader would assume is still open; and no source scan of
Accounting may reach the seam reflectively. The value-crossing half is not mechanically checkable —
see _Accepted limitations_.

**Withdrawn during design: a rule requiring golden-file coverage of every capability in both states.**
It was unenforceable and would have been mistaken for a guarantee. Under the reachability-only rule
the "off" state of a gate is the absence of a posting, which no golden figure can distinguish from a
target that was simply not eligible; and nothing could have failed CI when a new capability shipped
with coverage in one state. The rule that survives is the one a test can hold: the money-path subset
of the resolved set is recorded on the run, and the cross-run guard compares it.

### 5. Tenancy: `entitlements` carries `org_id` with an RLS platform escape

`entitlements` and `capability_cohorts` are organization-scoped tables that a platform process must
sometimes read across organizations. They keep `org_id` and RLS, with two policies: a read policy
(`org_id = app.org_id OR app.platform = 'on'`) and a separate `FOR ALL` write policy gated on
`app.platform` alone. `feature_flags` is globally readable and platform-write-only; only
`platform_audit_events` is platform-only in both directions.

- **`orgs` was the wrong analogy.** It was cited early as precedent for a table without RLS, but
  `orgs` has no `org_id` — its primary key _is_ the tenant, so there is no column to write a policy
  over. That is a different shape, not a precedent.
- **`asp_net_users` is the real precedent**, and the repository treats it as a soft spot rather than
  a pattern to copy. Following it would have been copying the weakest thing in the schema.
- **The failure mode inverts, which is the actual argument.** A path that forgets to open platform
  scope returns **zero rows**, not every organization's rows. Visible emptiness is a bug someone
  reports; a silent cross-tenant read is a breach. The tables also stay inside the schema guard's
  ordinary organization-scoped arm, so no new exemption class exists to be forgotten.

`app.platform` is set in exactly one place, in its own transaction, and an architecture test fails the
build on a second call site anywhere in `src/` or `infra/`. It is never set inside a request
transaction: `SET LOCAL` persists to the end of that transaction and would leave the remainder of the
request running with organization isolation disabled.

**That single-call-site property is held by a source scan, not by a database privilege.** No parameter
ACL is granted in the role bootstrap, so the runtime role is capable of setting the GUC itself; what
stops a second call site is a test reading `src/**/*.cs` and `infra/**/*.sql`. It is the same class of
gate, with the same limits, as the money-path scans in _Accepted limitations_ below — sound against
drift, not a privilege boundary. Anything that would change that assessment belongs in the security
review, not here.

Reads on the money path therefore need no escape at all. `feature_flags` is readable from the tenant
plane by design — a tenant must not be able to _toggle_ a flag; reading one reveals nothing that the
UI does not — and the two organization-scoped tables already permit an organization to read its own
rows. Durable resolution runs entirely inside the ambient request transaction.

### 6. Grants are append-only; no `revoked_at`

An entitlement change is a new row: `(org_id, capability, granted, effective_at, actor)`. Resolution
takes the latest row at or before now, ordered `effective_at DESC, granted ASC` so a revoke wins any
residual tie — fail-closed — with the id only as a total-order tiebreak and never as the semantic one.

`revoked_at` was rejected because it is an `UPDATE` on a billing-relevant fact. It destroys the
history of what was true when, it cannot express a scheduled future change, and it puts the write
grant to mutate a paid entitlement in the runtime role's hands. Append-only makes "what was this
organization entitled to on the 14th?" a query rather than an archaeology exercise. It also has a
cost, stated plainly: a mistake cannot be edited, only superseded, and the platform audit row that
accompanies it can never be corrected at all.

### 7. Introducing a `RequiresGrant` capability over shipped behavior requires a backfill

Adding `RequiresGrant: true` to a capability covering behavior organizations already have removes it
from every one of them the moment the deployment lands, because no organization has a grant row. Such
a change **requires a data migration granting it to all existing organizations in the same change**.
There is no automatic grandfathering, deliberately: an implicit one would silently entitle
organizations that should have been asked.

### 8. A per-replica cache with a `NOTIFY` backplane — and ADR-002's revisit trigger, met and not taken

Non-money reads are served from an in-process cache keyed by (organization, user) with a 30-second
TTL, invalidated by a Postgres `LISTEN`/`NOTIFY` channel the CLI signals on every write. Money reads
bypass it entirely (§9 below is the reason a stale "on" is not acceptable there).

**This meets ADR-002's revisit trigger, and we are deliberately not taking it.** "Multiple concurrent
replicas needing shared state or a backplane" is exactly what this is, across one to five replicas.
Redis stays deferred anyway, and not by pointing at ADR-002 as though it endorsed the outcome:

- The shared state here is **already in Postgres**, which is the durable store either way. Redis
  would be a second copy of a fact the database owns, with its own staleness.
- `NOTIFY` is best-effort, and the design does not depend on it. The TTL is the backstop, a missed
  notification costs at most 30 seconds on non-money paths, and money paths never read the cache — so
  the property Redis would buy is one nothing here needs.
- The cost is real and asymmetric for a solo operator: a managed service to provision, secure, patch,
  and fund, plus a new failure mode on the request path, against a latency saving on cached reads.

The honest downside: each host holds one additional non-pooled connection for its listener, and
invalidation bumps a single replica-wide generation, so one flag flip expires every cached key at
once and every caller misses simultaneously. That is why the cached member is asynchronous — a
synchronous one would let a flip drive concurrent callers into connection-pool exhaustion, each
holding one connection and none able to obtain a second.

### 9. Readiness is its own probe, with two independent preconditions

`/api/health/ready` is a readiness endpoint distinct from `/api/health` (liveness). It reports
unhealthy until **both** of its preconditions hold, and the container declares startup, readiness, and
liveness probes explicitly:

- `capability-seam` — a startup probe has proven the capability seam readable.
- `role-seeding` — the four fixed roles exist on this replica.

`IsPopulated` deliberately means **the seam is reachable**, proven by a probe that runs independent of
inbound traffic — not "some organization's set is cached". The latter would deadlock: a fresh replica
would be unready, therefore take no traffic, therefore never populate, therefore never become ready.
Cache keys are per (organization, user), so no set-based warm-up is even definable.

This mirrors the existing Hangfire degraded-mode decision (ADR-001): a dependency that is degraded but
reachable should hold a replica out of rotation rather than crash it, and readiness is tuned for
patience while liveness is tuned for speed.

**The unreachable-at-boot case is covered too, and it took two coupled changes.** Role seeding is this
process's first database call. It ran unguarded, so a replica booting against an _unreachable_
database died there and never bound a port — one call before the registry validator that already
tolerated the same outage — and the platform crash-looped the revision, which no probe tuning can
recover from. Both halves of the fix are required and neither is safe alone:

1. **The guard.** Startup role seeding now rides out an unreachable server: it logs, continues, and
   the host binds. The "is the server reachable" test is the same chain-walking helper the registry
   validator uses, shared rather than reimplemented, because the obvious version of the filter never
   fires — EF wraps the provider failure in an `InvalidOperationException` and Identity adds a
   `DbUpdateException` on top. `PostgresException` is tested before its `NpgsqlException` base and
   returns "reachable", so a missing table or a revoked grant still fails the boot loudly. Only an
   outage is ridden out.
2. **The gate.** Readiness tracks role seeding as its own check. Doing (1) alone would have been
   strictly worse than the crash loop it replaces: the four roles are a precondition for sign-in and
   for every role-based policy, and seam reachability says nothing about them, so a replica that
   swallowed the seeding failure would go healthy the moment the seam came back and enter rotation
   unable to authenticate anyone — a silent partial outage in place of a loud one. A background probe
   retries seeding with capped backoff until it succeeds, so a replica that came up during an outage
   still converges instead of staying useless for its whole life.

The ordinary boot is unchanged: seeding still happens synchronously before the host binds, so the
retry probe finds the work done and exits immediately. The CLI verbs are unaffected — the four seeders
each call the throwing entry point themselves, so `seed` still fails loudly against a database it
cannot reach, and no other verb needs roles.

The limit that remains is the honest one: readiness holds a replica out of rotation, it never restarts
it. An outage lasting past the readiness budget (10 + 10×20 = 210s) leaves the replica out of rotation
and still retrying, which is intended. The work itself is never the constraint — four existence checks
and at most four inserts.

### 10. The cross-run period guard, and the limit of its scope

A bulk run records the money-path subset of the capability set it ran under. A later confirm for the
same (organization, run type, period) whose money-path state differs is rejected with
`capabilities_changed_since_prior_run` (409) unless the operator explicitly acknowledges it. A
separate guard rejects a confirm whose preview was resolved under a different capability version
(`capabilities_changed`, 409); the preview-token check runs first, because that one the SPA can
recover from automatically.

**The scope limit, stated in the guard's own terms: one `run_type`, one capability state per period —
_not_ one period, one capability state.** A rent run and a late-fee run in the same period may run
under different capability states and neither guard fires. That is acceptable today for one reason
only: the reachability-only rule means a capability gating late-fee reachability cannot change what
rent posted. It is not acceptable in general.

**Widening trigger: the first money-path capability whose gate is read by more than one
`IRunStrategy`.** At that point one period genuinely can hold two runs whose outcomes depend on the
same capability, and the guard must widen to (organization, year, month) — which needs a new index,
since `run_type` is the second column of the existing one. That trigger is mechanically checkable, so
per this repository's preference for enforcement over discipline it should become a CI gate rather
than a line in this document.

**A second scope limit, in the other axis: the compared state is per USER, not per deployment.** The
resolved set is keyed `(organization, user)` and user-level cohort rows participate in it, so two staff
users in one organization can produce a different money-path state for the same period with no operator
action and no state change anywhere — one of them is simply in a cohort. The guard would then reject the
second user's confirm, `RegistryMoved` would be false (the names match), and the operator would be
offered the "restore the earlier feature state" remedy for a difference no feature state can explain.
It is unreachable today only because the CLI refuses `IsFixture` capabilities for `cohort add` and the
fixture is the only money-path entry — a check that exists for a different reason, which is not a
guarantee.

**Recommended direction, not taken here: money-path capabilities should not resolve per user** —
ignore user-level cohort rows when `IsMoneyPath` is true, so a money-path state is a property of the
organization and the deployment, which is what the guard already assumes it is. That is a change to the
resolution order in §2 and deserves its own decision rather than being folded into a guard fix.

One residual, deliberately accepted: prior runs recorded before this shipped carry no capability
state and are skipped, so for each (organization, run type, period) that already held a run, the first
confirm after deployment is unguarded. The window closes after one run and cannot be reopened.
Fabricating a state for those rows would reject the ADR-019 §2 recovery re-run for every period
holding a pre-deployment run, while being unable to unwind the run it objected to.

### 11. Adding _or_ removing a money-path capability is period-breaking

The recorded state lists every money-path capability from the **registry**, so both directions move it
and no feature-flag write can compensate:

- **Removing one** makes the prior run's list unreachable — the operator cannot restore a state whose
  capability no longer exists.
- **Adding one** appends an entry to every future list, so every period holding a prior run conflicts,
  fleet-wide, on the next confirm.

Safe sequences:

- **Adding**: deploy the registry entry and its gate in a release where no period has an in-flight
  run to re-confirm, or accept that the first confirm per (organization, run type, period) after the
  deployment must be acknowledged.
- **Removing**: retire the gate first and the registry entry second, in separate deployments, so no
  running build reads a capability the registry has dropped; the same two-step ordering the startup
  validator requires for deleting a `feature_flags` row.

Both cases surface a message that names acknowledgement, not restoration, because restoration is the
remedy that does not exist here.

### 12. Acknowledgement is API-only, and who may give it

The confirm request carries `acknowledgeCapabilityChange`. The SPA sends `false` unconditionally: it
never offers the override, because designing a money-decision surface for a decision whose real shape
is unknown until the first genuine money-path capability would be designing against a guess. Today the
conflict is unreachable in production — only the permanent fixture capability can trip it, nothing
reads it, and no production path sets it.

**The consequence, stated rather than implied: there are reachable states with no in-product remedy.**
Combined with §11, an operator can face a 409 whose only in-product suggestion is to restore an earlier
capability state that cannot be restored, with acknowledgement available exclusively over the API.

**Who may acknowledge:** the confirm endpoint is `RequirePMStaff`, so any staff user can. That is
defensible — the same users already confirm the run itself — but it is a fiduciary authorization
decision, recorded here as an explicit ruling rather than left as an inherited default.

**Un-mute trigger, rather than an open-ended deferral:** the first non-fixture money-path capability
ships either an override affordance in the SPA or a recorded reason it does not — **and settles §10's
per-user limit in the same change**, because the moment a real money-path capability exists, one
`cohort add` naming a user makes two staff members in one organization disagree about the same period,
and the 409 they get suggests a remedy that cannot apply. Whichever way it is settled (excluding
user-level cohorts from money-path resolution, or refusing user-level cohorts on a money-path
capability at write time), it must be settled before the capability that makes it reachable ships,
not after.

### 13. Money-path capabilities expire after 90 days, and extending one is recorded here

A money-path capability is standing risk on the ledger, and §4's rule is tolerable only because these
are temporary. A CI gate dates each one from git history and fails when a non-fixture money-path
capability passes 90 days. `IsFixture` exempts the permanent test fixture; nothing else may use it.

**Extension procedure.** Three operator-facing messages name this ADR as the only alternative to
deleting the capability, so the procedure is: amend this section with the capability's name, the
reason the gate cannot yet be deleted, and a dated re-review, in the same change that adjusts the
gate. The window is not a parameter to be raised globally — raising it for one capability by raising it
for all is exactly the drift the gate exists to catch.

The gate **hard-fails under `GITHUB_ACTIONS`** and only skips locally, because a shallow clone does not
fail — it silently dates every capability to the graft commit and reports an age of roughly zero. Moving
this suite to a container job, a slimmer runner image without git, or a shallow checkout therefore turns
CI red on purpose. The correct response is to restore git plus full history, or to move the invariant
deliberately and record it here — never to add a skip, which restores the green-build-that-checked-nothing
this gate exists to close.

### 14. The write surface is a CLI verb, and `LEASEBOOK_OPERATOR` is a deployment requirement

`capabilities` (list, flag enable/disable, grant, revoke, cohort add/remove) is the only write surface:
no endpoint, no UI. In production it is reached through a manual-trigger Container Apps Job running the
**app** image as the **app** role — the registry is source code, so a job built from a different commit
knows a different set of capabilities than the application it is being used to control. The job carries
no schedule and must not grow one; a capability flip is a decision, never a timer. Operational detail —
the execution-template form, the pinned template file, and the first-execution checks — lives in
`infra/README.md` and the [diagnostics runbook](../runbooks/diagnostics.md).

`LEASEBOOK_OPERATOR` names the accountable person on every audit row. It is **a deployment
requirement, not a nicety**: rows written without it attribute the change to an anonymous process, and
because both the state rows and the audit rows are append-only, such a row can never be corrected. The
verb therefore refuses every mutating subcommand when it is unset outside Development, and the template
ships the variable empty rather than with a placeholder — a placeholder is precisely the permanent,
plausible-looking lie the refusal exists to prevent. `list` is exempt: reading capability state must
never be refused, because reading it is what an operator does first during an incident.

Fixture capabilities are refused by every mutating subcommand, including cohort membership — a cohort
row turns a capability on as effectively as a flag does.

### 15. Q1, Q2, Q3

- **Q1 — platform administrator identity: deferred to Project 2.** The CLI and the job run as operator
  processes, not authenticated users, so the audit actor is a process/operator identity rather than an
  `asp_net_users` id. Whether a platform administrator eventually lives in the existing identity store
  with a new role, or in a separate one, is Project 2's question and blocks nothing here.
- **Q2 — the module shares `AppDbContext`,** with a model-level guard. A second context would make a
  cross-organization join inexpressible rather than merely blocked, but it is a real ADR-004 deviation
  and costs a second migration history; the RLS shape already inverts the failure mode (§5). The guard
  is an architecture test asserting that the three platform entities carry **no** EF global query
  filter: adding `: IOrgScoped` to `Entitlement` is a one-token change that would silently empty every
  cross-organization platform read, with no other test failing.
- **Q3 — `leasebook_ops` retains `SELECT`** on the platform tables, using the standard append-only
  revoke unchanged rather than a narrower custom one. The platform-only read policy already means the
  read-only role sees nothing without `app.platform`, so the grant is support visibility, not exposure.

## Consequences

- A kill switch is now a genuine kill switch: flipping it reaches live traffic in seconds without a
  deployment, and it cannot silently downgrade an entitled organization, because entitlement is a
  different row in a different table evaluated at a different step.
- The seam is deliberately narrow. There is no admin UI, no metrics, no plan or tier definitions, no
  cross-organization tenant data access, no impersonation, and no per-user permission matrix; the role
  set stays as it was.
- Adding a capability is now a multi-artifact change: a registry entry, possibly a backfill migration
  (§7), and — if it is money-path — a countdown (§13) and a deployment sequence (§11).

### The characteristic failure mode: the silent empty read

This design's dominant hazard is not a wrong answer; it is **an empty answer that reads as a valid
one**. RLS filters rather than raises, so a read that forgets platform scope returns zero rows with no
error, and "no row" is a meaningful value at every level of this system: no flag row means "use the
default", no entitlement row means "not entitled", no cohort row means "not in the beta". A missing
`SET LOCAL` therefore renders as a coherent, plausible, wrong answer.

It recurred in four independent places during implementation — the cache refresh, the startup
validator, the durable state reader, and `capabilities list`, which would otherwise have reported that
nobody is entitled to anything. Naming the class is worth more than any individual fix, and the rule
that follows from it is: **every reader of these tables asserts its own scope and throws when it is
missing**, the same rule background jobs already follow for organization context. Mutating statements
are covered separately, because they are silent too — RLS filters an `UPDATE` or `DELETE` to zero rows
rather than raising, so any writer off the tracked-entity path must assert an affected-row count.

### Accepted limitations

These are limits, not covered ground:

- **The money-path source scans are defeated by string splitting.** A reflective lookup assembled from
  concatenated fragments produces no compile-time reference and no contiguous matching substring, while
  still reading a capability value inside posting code. This is inherent to scanning source text, and
  is the same limitation two existing gates accept. The threat model is accidental drift — someone
  innocently reaching for a capability in posting code — not hostile code inside Accounting.
- **The age gate's history probe breaks on a rename plus a greater-than-50% rewrite in one commit.**
  Git's rename following gives up, and the capability's clock silently resets in the _unsafe_
  direction. Nothing detects this mechanically, and the gate's own vacuity guard structurally cannot.
- **Renaming a capability's name string resets its clock to zero.** The name is the identity the probe
  searches for, so a rename is a one-line bypass of the 90-day window.
- **Value crossing is not mechanically checkable.** No reference-graph test can distinguish a legitimate
  strategy selection from a capability-derived value passed as a posting argument, because the
  difference is a value crossing rather than a type crossing. A call-site allowlist for money-path
  capabilities is the available machine-checkable form of §4 if it is ever needed.

### Deleting an organization is refused while it has grant history

`fk_entitlements_orgs_org_id` is `ON DELETE RESTRICT` (`M8_RestrictOrgDeleteOnEntitlements`). It
shipped as `ON DELETE CASCADE`, which meant one `DELETE FROM orgs` erased every entitlement event for
that organization — the append-only record of what it was entitled to and when, which §6 exists to
preserve, and which contradicted the reasoning that gave `platform_audit_events.org_id` no foreign key
at all. `RESTRICT` fails the deletion loudly instead. That is the right failure: an organization with
grant history is not a row to delete casually, and whoever needs to delete one now has to decide what
happens to the history rather than having it decided silently.

**`fk_capability_cohorts_orgs_org_id` stays `ON DELETE CASCADE`, and the asymmetry is deliberate.**
Cohort rows are mutable membership — current targeting state, freely added and removed, carrying no
history of its own, since the record of who changed a cohort lives in `platform_audit_events` and no
organization deletion can touch it. Membership in a deleted organization is meaningless; a grant event
is a record, and a record is not membership. Both delete actions are pinned by
`SchemaGuardTests.ExpectedPlatformForeignKeys`, with the reason for the difference stated there, so
neither can drift and "making them consistent" cannot pass as a tidy-up.

### Nothing here is deployment-verified

The Container Apps job, its probes, and its execution template are verified three different ways, and
the distinction matters: some constraints are confirmed against the ARM resource reference (the probe
`failureThreshold` ceiling, for instance, which invalidated a value that compiled cleanly), some are
confirmed only by compilation, and some are confirmed by nothing at all until an apply. `az bicep build`
type-checks; it does not range-check ARM constraints, so an out-of-range value compiles green. Twelve
specific unknowns are enumerated in [`infra/README.md`](../../infra/README.md) under the first-apply
checks, which is where an operator will read them. Azure remains operator-gated.

## Revisit trigger

Reopen when any of these becomes observable:

- **A money-path capability's gate is read by more than one `IRunStrategy`** — widen the cross-run guard
  to (organization, year, month) per §10, and add the CI gate that detects the condition.
- **The first non-fixture money-path capability ships** — settle §12 (an override affordance or a
  recorded reason there is none), and start its §13 countdown deliberately rather than incidentally.
- **A capability needs to answer differently per user in a way cohorts cannot express**, or a per-user
  permission matrix is required — this model has no such surface and should not grow one informally.
- **Replica count grows past a handful, or a shared-state need appears that the TTL backstop cannot
  absorb** — §8's reasoning for deferring Redis is about cost against a need that does not exist yet,
  and that is a factual claim with an expiry date.
- **A capability is proposed that must change an amount rather than reachability** — that is a request
  to move a parameter into `OrgSettings`, not to relax §4. If §4 is ever relaxed, it needs a superseding
  ADR, not an amendment.
- **An organization is deleted, or is about to be** — the delete is now REFUSED while the organization
  has entitlement rows (`ON DELETE RESTRICT`, above). Decide what happens to the grant history before
  reaching for a cascade: archiving it elsewhere and a deliberate delete are both defensible; silently
  destroying it is what this replaced.
