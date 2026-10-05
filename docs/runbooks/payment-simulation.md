# Isolated payment simulation

- **Audience:** Developers and test operators
- **Status:** Implemented, non-live only
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-04

The simulator exercises the real Payments and Accounting path. It has no provider network client
and cannot move real money. Production and Staging reject Simulation; live/provider configuration
is refused. The [specification](../payments/simulated-payment-spec.md) owns the financial contract.

## Create the fixture

With the normal development Postgres container running, from the repository root:

```powershell
./scripts/dev.ps1 up
./scripts/payment-fixture.ps1 init
```

The script creates the separate `leasebook_payment_fixture` database, applies migrations as the
migrator role, seeds two fresh organizations and writes `payment-fixture.local` (gitignored).
The manifest holds the generated org/bank/generation bindings and a fixture-only signing key.
The script refuses an existing database or manifest; it does not alter `leasebook`, demo, load,
scenario or portal data. `-Port` follows the existing development Postgres port override; `-Container`
supports CI's Postgres service container. No new ports are bound.

Start the API in a terminal using the runtime role:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Default = 'Host=localhost;Port=5632;Database=leasebook_payment_fixture;Username=leasebook_app;Password=dev_app_pw'
$env:Payments__ManifestPath = (Resolve-Path ./payment-fixture.local).Path
dotnet run --project src/LeaseBook.Web --no-launch-profile --urls http://localhost:5080
```

Start `npm run dev` from `web/` in another terminal. The normal README port map applies. Sign in as
`tenant-a1@payments.test`, `tenant-a2@payments.test`, `tenant-b1@payments.test` or
`tenant-b2@payments.test`, password `Payment-Fixture-2026!`. Each starts owing $1,000. The staff
accounts `admin-a@payments.test` and `admin-b@payments.test` use the same development-only password.
Staff see payment exceptions on Operations. Only PMAdmin can retry technical failures.

## Drive an outcome

Submit an amount in the tenant portal. The worker records provider acceptance, but no bank receipt
is generated automatically. Copy the operation reference. In a terminal with the same three
environment variables as the API, resolve the trusted org from the local manifest and emit facts:

```powershell
$fixture = (Get-Content ./payment-fixture.local -Raw | ConvertFrom-Json).Payments.Fixtures[0]
$operation = '<operation UUID shown in the portal>'
$date = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
dotnet run --project src/LeaseBook.Web --no-launch-profile -- payment-simulation emit $fixture.OrgId $operation PayoutPaid $date
dotnet run --project src/LeaseBook.Web --no-launch-profile -- payment-simulation emit $fixture.OrgId $operation BankCredit $date
```

`PayoutPaid` leaves the ledger unchanged. `BankCredit` provides separate complete gross, fee-free
bank evidence and records one receipt. The portal polls and refreshes its ledger after the effect
commits. Reloading the page resumes observation without submitting again. Repeating the same CLI
event is idempotent. `payment-simulation step` runs one worker pass without starting a web server.

Other event kinds are `Processing`, `Succeeded`, `Available`, `PayoutPending`, `PayoutFailed`,
`Failed`, `Return`, `Refund` and `Dispute`. A return after receipt preserves that receipt and routes
the operation to review; it never invokes an unrestricted reversal. A PMAdmin then resolves it on the
Operations page (ADR-052): **Post return** posts a full return as the receipt's linked reversal, dated
on the return evidence date, or reports the reason it cannot; **Close review…** records a note and
posts nothing, for a return staff have corrected by hand. Sign in as `admin-a@payments.test` with the
fixture password to try it. Technical retries use delays
of 1, 5, 30, 120 and 600 seconds, then require PMAdmin retry of the same operation. Unsupported
money cases offer no force-post action. Unmapped objects remain in the durable inbox; after ten
minutes the staff read reports their count for fixture-operator review, and **Show unmatched
notifications** on the Operations page lists each one's received time, kind, amount, provider
reference and age. The list is read-only.

The CLI constructs signed observations without publishing the signing key. Tests can send them to
`POST /callbacks/payments/simulation`, with `X-Simulation-Signature` in `unixSeconds.hexHmac` format
over the timestamp, a dot, and the exact body bytes. This synthetic signature is not Stripe's wire
format. The endpoint is outside cookie-authenticated `/api`; it uses signature authentication,
bounded body size and rate limiting, then server-owned account routing. A caller's org metadata or
cookie cannot select the callback's organization.

## Restart, reset and failure recovery

An ordinary restart uses the same database and manifest. Provider acceptance, operations, inbox,
effect links and retry leases survive; leases expire after 30 seconds. Changing manifest bindings
cannot redirect existing operations. An unknown provider result keeps its original identity.

To discard all payment-fixture data, stop the API and any fixture CLI processes, then run:

```powershell
./scripts/payment-fixture.ps1 reset
```

Reset checks the fixed database name, exact org count and each manifest generation/bank marker
before dropping that database. Active connections make the drop fail. It never deletes individual
journal rows. It recreates the fixture with new identities and a new signing key; old callbacks
cannot match. A missing/mismatched manifest, unexpected org or failed marker check requires operator
inspection, not a force option. If initial setup fails before markers exist, inspect the dedicated
database before manually discarding it; do not use the shared `reset-db` command as a substitute.

## Browser validation

Stop any existing host before running this against the dedicated fixture so Playwright cannot
reuse a host pointing to a different database. Keep the API environment above, then from `web/`:

```powershell
$env:PAYMENT_SIMULATION_E2E = '1'
npx playwright test simulated-payment.spec.ts
```

CI initializes a separate payment database and runs this test after the ordinary fixture suite.
It covers keyboard submission, reload, separate payout/bank evidence, real ledger refresh, late
return wording and WCAG 2 AA checks. Backend integration tests additionally inject failures around
provider acceptance and ledger commit, validate routing/isolation and exercise locked periods.
