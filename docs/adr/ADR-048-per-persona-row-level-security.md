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

The gate on `entitlements`, `capability_cohorts` and `platform_audit_events` also admits
`app.platform = 'on'`, because the platform plane runs with no org and no persona. `Rls.EnableOrgRls`
and the platform helpers apply the deny-by-default gate to every new table, so a new table is closed
to portal personas until a migration writes its grant through `Rls.ApplyPersonaGate`.

**The database derives portal identity.** Grants resolve the caller's owner or tenant from the
active link row — `owner_access` or `resident_access` where `user_id = app.user_id` and
`revoked_at IS NULL`. The application never sets an owner or tenant id, so a revocation takes
effect on the next statement. The link tables' own grant is `user_id = app.user_id`, which keeps
the policy graph acyclic.

**Minimal grants**, derived from the endpoints' query footprints (`P2_PersonaRls`) and pinned
predicate by predicate in `SchemaGuardTests`:

| Table                          | Owner reads                      | Tenant reads                       | Portal inserts                                                                                     |
| ------------------------------ | -------------------------------- | ---------------------------------- | -------------------------------------------------------------------------------------------------- |
| `owner_access`                 | its own link rows                | —                                  | —                                                                                                  |
| `resident_access`              | —                                | its own link rows                  | —                                                                                                  |
| `owners`                       | its non-system owner             | —                                  | —                                                                                                  |
| `tenants`                      | —                                | its tenant                         | —                                                                                                  |
| `journal_lines`                | lines with its `owner_id`        | lines with its `tenant_id`         | —                                                                                                  |
| `journal_entries`              | entries having such a line       | entries having such a line         | —                                                                                                  |
| `accounts`                     | —                                | accounts its lines reference       | —                                                                                                  |
| `properties`                   | currently owned, or on its lines | its lease's property               | —                                                                                                  |
| `statement_artifacts`          | its issued statements            | —                                  | —                                                                                                  |
| `lease_lite`, `units`          | —                                | its leases and their units         | —                                                                                                  |
| `property_ownership_transfers` | —                                | its lease property's history       | —                                                                                                  |
| `bank_accounts`                | —                                | the payment fixture's bank account | —                                                                                                  |
| `payment_fixtures`             | —                                | all (the simulation fixture)       | —                                                                                                  |
| `payment_operations`           | —                                | its tenant's operations            | tenant: its own tenant, as itself (`user_id = app.user_id`)                                        |
| `payment_observations`         | —                                | observations of those operations   | —                                                                                                  |
| `org_settings`                 | the row                          | the row                            | —                                                                                                  |
| `audit_events`                 | —                                | —                                  | tenant: `actor_user_id = app.user_id`; owner: only `account-security` rows about itself, as itself |

Every other table and command is denied. Two footprints needed code as well as grants:

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
  on the first `OR` arm, evaluated once per statement as an InitPlan; `docs/perf.md` records the
  before and after.
- The historical `EnableOrgRls` calls now emit the deny-by-default gate on a fresh database;
  `P2_PersonaRls` drops and recreates each policy, so fresh and upgraded databases converge. Its
  `Down` drops the gate rather than restoring deny-by-default, because the code before it sets no
  persona.
- Per-persona database roles and splitting PMAdmin from PMStaff at the database remain out of scope.

## Revisit trigger

Reopen when a portal gains a write beyond payment submission and account security, when a third
persona is proposed, or when a portal persona needs organization-wide data that a grant can only
express by reading a whole table.
