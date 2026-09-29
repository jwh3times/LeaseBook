# ADR-048: Per-persona row-level security inside the organization

- **Status:** Accepted
- **Date:** 2026-09-29
- **Deciders:** Jerry Holland

## Context

ADR-003 kept row-level security at the organization boundary and enforced the portal personas —
a tenant sees only their own ledger, an owner only their own money — in the application layer. Its
revisit trigger was a portal surface past "a handful" of endpoints. The read-only owner portal took
it to seven (four tenant, three owner), and the maintainer promoted #314 on 2026-09-29: build the
database boundary now, while the surface is still small enough to retrofit.

The starting point: `OrgScopedExecutor` is the only production setter of `app.org_id`, every
org-scoped table carries one permissive org policy under `FORCE ROW LEVEL SECURITY`, the three
capability tables carry platform policies instead (ADR-028), and roles lived only in .NET. A portal
query that forgot its owner or tenant filter would return the whole organization's rows.

## Decision

**A persona is part of the org context.** `OrgScopedExecutor` sets `app.org_id`, `app.persona`
and `app.user_id` in one `set_config(…, true)` statement, transaction-locally, and is their only
production setter (`OrgContextCallSiteTests`; the test side has `RlsProbe`, and the operator
restore check `infra/db/verify.sql` sets `staff`). `app.user_id` is `''` for a system actor, which
every grant maps to `NULL`.

| Persona  | Who                                                               |
| -------- | ----------------------------------------------------------------- |
| `system` | a system actor: jobs, workers, seeders, CLI verbs                 |
| `staff`  | a user whose every role is PMAdmin or PMStaff                     |
| `tenant` | a user whose only role is Tenant                                  |
| `owner`  | a user whose only role is Owner                                   |
| `none`   | any mix of a portal role with another role, or no recognised role |

`PersonaResolver` maps the authenticated principal's roles before the request transaction opens;
`OrgContextMiddleware` passes the result. The executor requires the persona on every user-actor
call, refuses `system` for a user and a portal persona for a system actor, and
`RunAsSystemAsync` states `system`. A principal whose id claim will not parse (ADR-039's
`principal-without-user-id`) cannot hold a portal persona, and never becomes `system`.

**Every org-scoped table carries a deny-by-default persona gate**, three `RESTRICTIVE` policies that
AND with the unchanged org or platform policies:

- `{table}_persona` (`FOR ALL`) is the grant table: `staff` and `system` everywhere; a portal
  persona only through that table's read grant (`USING`) and insert grant (`WITH CHECK`, always
  written out so a read grant never becomes a write grant by default); nothing for an unset, `none`
  or unknown value.
- `{table}_persona_no_update` (`FOR UPDATE`, `USING (true)`, `WITH CHECK` gate) and
  `{table}_persona_no_delete` (`FOR DELETE`, `USING` gate) keep updates and deletes
  organization-wide personas only.

The issue asked for one restrictive policy per table. One `FOR ALL` policy cannot express this
grant table: Postgres uses its single `USING` for both `SELECT` and `DELETE`, and its single
`WITH CHECK` for both `INSERT` and `UPDATE`. A tenant's read of its own payment would then also be
a delete of it, and its insert grant an update grant — the isolation pack's write tests fail on
exactly that. The companions are the smallest addition that closes both, and the `FOR UPDATE`
companion's `USING (true)` leaves `SELECT … FOR SHARE` (which applies UPDATE policies' `USING`)
decided by the read grant alone, as the payment eligibility check needs.

Accepted consequence of that choice: a portal persona can take `FOR UPDATE`/`FOR SHARE` row locks
on rows it can already read. That is contention, not a write — every `UPDATE` and `DELETE` it
attempts on them is still refused — and the rows are its own. Narrowing it would need the
companion's `USING` to exclude portal personas, which would also break the `FOR SHARE` lock the
payment eligibility check takes on the tenant's property.

The gate on `entitlements`, `capability_cohorts` and `platform_audit_events` also admits
`app.platform = 'on'`, because the platform plane runs with no org and no persona. `Rls.EnableOrgRls`
and the platform helpers apply the deny-by-default gate to every new table, so a new table is closed
to portal personas until a migration writes its grant through `Rls.ApplyPersonaGate`.

**The database derives portal identity.** Grants resolve the caller's owner or tenant from the
active link row — `owner_access` or `resident_access` where `user_id = app.user_id` and
`revoked_at IS NULL` — joined to a non-system `owners` or `tenants` row. A link that names a system
roll-up row (the foreign key allows one; the services refuse to create it) therefore reaches nothing
but the link itself, and even the organization-level grants (`org_settings`, the payment fixture
and its bank account) require an effective link rather than the persona alone. The application
never sets an owner or tenant id, so a revocation takes effect on the next statement. The link
tables' own grant is `user_id = app.user_id`, and the `owners`/`tenants` grants use the bare link
and check `is_system` on their own row; that keeps the policy graph acyclic.

**Minimal grants**, derived from the endpoints' query footprints (`P2_PersonaRls`) and pinned
predicate by predicate in `SchemaGuardTests`:

| Table                          | Owner reads                      | Tenant reads                       | Portal inserts                                                                                 |
| ------------------------------ | -------------------------------- | ---------------------------------- | ---------------------------------------------------------------------------------------------- |
| `owner_access`                 | its own link rows                | —                                  | —                                                                                              |
| `resident_access`              | —                                | its own link rows                  | —                                                                                              |
| `owners`                       | its non-system owner             | —                                  | —                                                                                              |
| `tenants`                      | —                                | its tenant                         | —                                                                                              |
| `journal_lines`                | lines with its `owner_id`        | lines with its `tenant_id`         | —                                                                                              |
| `journal_entries`              | entries having such a line       | entries having such a line         | —                                                                                              |
| `accounts`                     | —                                | accounts its lines reference       | —                                                                                              |
| `properties`                   | currently owned, or on its lines | its lease's property               | —                                                                                              |
| `statement_artifacts`          | its issued statements            | —                                  | —                                                                                              |
| `lease_lite`, `units`          | —                                | its leases and their units         | —                                                                                              |
| `property_ownership_transfers` | —                                | its lease property's history       | —                                                                                              |
| `bank_accounts`                | —                                | the payment fixture's bank account | —                                                                                              |
| `payment_fixtures`             | —                                | the org's simulation fixture       | —                                                                                              |
| `payment_operations`           | —                                | its tenant's operations            | tenant: a fresh request only — its own tenant, as itself, worker columns unset, fixture bank   |
| `payment_observations`         | —                                | observations of those operations   | —                                                                                              |
| `org_settings`                 | the row                          | the row                            | —                                                                                              |
| `audit_events`                 | —                                | —                                  | both: own `password-changed`/`mfa-enrolled`; tenant also its `payment_operations` `insert` row |

Every other table and command is denied. The payment insert grant pins every column the submit
path fixes — `status = 'Requested'`, no reason, provider id, journal entry, lease or attempts, and the
org fixture's generation, bank and account. `provider_id` matters most: observations carry no
operation id, so the observations grant keys on the provider id of the tenant's operations, and only
the worker, as system, may ever set it.

Two footprints needed code as well as grants:

- `GetOrgSettings` lazily inserted the settings row on first read. The owner portal now reads with
  `CreateIfMissing: false`, which resolves a missing row to its defaults without writing.
- Account security (change password, MFA enrollment) writes an `account-security` audit row for
  any signed-in user, owners included. That is the owner's one insert grant, narrowed to rows about
  itself — an addition to the issue's owner-read list, which would otherwise make password changes
  fail for owners.

`CapabilityStateReader` now also asserts an organization-wide persona outside platform scope, since
the gate is one more way for an entitlement read to return zero rows without raising.

**The application-layer checks stay.** The portal policies, the scoped authorization handlers and
the allow-list projections from ADR-003 remain the first layer: they decide which endpoint answers
and what it may say. The database decides which rows exist to be said. Either alone fails open in
the case the other exists to catch.

## Consequences

- A portal query that forgets its filter returns nothing. The `none` persona makes a mixed-role
  account read nothing anywhere, including through staff endpoints, instead of resolving to the
  wider side.
- Every connection that reads org data must state a persona. That includes support queries in
  pgAdmin as `leasebook_ops`: `set_config('app.persona', 'staff', true)` alongside `app.org_id`.
- Policies are longer, and portal reads pay for subqueries. The staff and system paths short-circuit
  at run time on the gate, which is read once per statement as an InitPlan and wrapped in
  `COALESCE(…, false)`. The wrapper is load-bearing for the planner, not for meaning: bare,
  `$param = ANY(array)` gets Postgres's default equality selectivity, so the gate looked like it
  removed ~99% of rows. That turned the load fixture's bank register into a nested loop (p95 ~20 ms
  → ~1.3 s) before the wrapper, which the planner estimates at 50%, restored the plans the tables
  had without the gate.
- The staff read path still pays planning time for the nested policy expansion, a few milliseconds
  per statement that touches the journal. On the `load` fixture, measured against the final grants,
  p95 went from 6–8 ms to 19–22 ms (tenant ledger), 33–39 ms to 54–59 ms (dashboard), 18–21 ms to
  22–24 ms (bank register) and 21–24 ms to 41–47 ms (owner statement). All are well within the
  300 ms budget; `docs/perf.md` records the runs.
  If that margin ever matters, the next step is to move grant resolution into helper functions
  with declared costs, not to widen a grant.
- The historical `EnableOrgRls` calls now emit the deny-by-default gate on a fresh database;
  `P2_PersonaRls` drops and recreates each policy, so fresh and upgraded databases converge. Its
  `Down` drops the gate rather than restoring deny-by-default, because the code before it sets no
  persona.
- Per-persona database roles and splitting PMAdmin from PMStaff at the database remain out of scope.

## Revisit trigger

Reopen when a portal gains a write beyond payment submission and account security, when a third
persona is proposed, or when a portal persona needs organization-wide data that a grant can only
express by reading a whole table.
