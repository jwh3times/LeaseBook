# Isolated payment simulation

- **Audience:** Developers and test operators
- **Status:** Implemented, non-live only
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-07

The simulator exercises the real Payments and Accounting path. It has no provider network client
and cannot move real money. Production and Staging reject Simulation; live/provider configuration
is refused. A `StripeSandbox` mode is recognised for a later release and a host configured for it
refuses to start. The [specification](../payments/simulated-payment-spec.md) owns the financial contract.

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

`PayoutPaid` leaves the ledger unchanged. `BankCredit` provides separate complete bank evidence for a
clean payment — the charged amount, the quoted fee, and the ledger amount as the net — and records one
receipt for the ledger amount. A fresh fixture has no fee rule, so the fee is zero and the charge is the
ledger amount. To try a fee, sign in as `admin-a@payments.test`, open **Settings** and set a rate
or a fixed amount under **Online payment fees**; the tenant form then quotes the fee before submitting. The portal polls and refreshes its ledger after the effect
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

### Drive a payout

A real processor pays out many payments in one bank deposit and keeps a fee from each. `payout`
delivers that evidence for payments already submitted in the portal
([ADR-053](../adr/ADR-053-processor-fees-and-batched-settlement.md)). It runs one worker pass first,
so the payments need no separate `step`.

```powershell
$run = { dotnet run --project src/LeaseBook.Web --no-launch-profile -- payment-simulation payout $fixture.OrgId $date @args }
& $run po_1 "pay:$first" "pay:$second"              # both clean: the processor kept the quoted fee
& $run po_2 "pay:$third:9.28"                       # the processor kept 9.28, not the quoted fee
& $run po_3 "return:$first" "fee:4.00"              # a return, and the processor's return fee
& $run po_4 "pay:$fourth" --bank-amount=99.00       # the bank amount does not match the lines
& $run po_5 "refund:$second"                        # a line LeaseBook does not post
& $run po_6 "pay:$fourth" --type=instant            # a payout type LeaseBook does not post
```

Each `$first`…`$fourth` is a payment reference from the portal. A line is `pay:<payment>[:<fee kept>]`,
`return:<payment>[:<fee given back>]`, `refund:<payment>` or `fee:<amount>`. With no fee named, the
processor kept, or gave back, exactly the quoted fee. The bank amount is the sum of the lines unless
`--bank-amount` overrides it. The command prints the payout's status and, if it is held, the reason.

A payout posts completely or not at all. One whose fee differs from the quote posts the receipt for
what the tenant paid toward their ledger and takes the difference from, or adds it to, the management
fees held in the trust bank; a shortfall needs fees held there. A payout that contains a return waits
for an administrator. Sign in as `admin-a@payments.test` and open **Operations**: the **Payouts**
list shows each payout, its status, why it waits and its lines. **Post payout** posts a waiting payout
whole, or reports the reason it cannot; **Close review…** records a note and posts nothing. Other
staff see the same list without the actions.

To see a payout reconcile, open **Banking**, import a statement whose one line is the payout's bank
amount on its bank date, and confirm: the line matches the payout as a group and clears every bank
line the payout posted.

The CLI constructs signed observations and payout evidence without publishing the signing key. Tests
can send them to `POST /callbacks/payments/simulation` and `POST /callbacks/payments/simulation/payout`,
with `X-Simulation-Signature` in `unixSeconds.hexHmac` format
over the timestamp, a dot, and the exact body bytes. This synthetic signature is not Stripe's wire
format. Each endpoint is outside cookie-authenticated `/api`; it uses signature authentication,
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
return wording and WCAG 2 AA checks. A second test sets a fee rule on the Settings page, pays twice
with a quoted fee, delivers a payout whose surplus covers its shortfall, and has an administrator
post a payout that contains a return from the keyboard. Backend integration tests additionally inject failures around
provider acceptance and ledger commit, validate routing/isolation and exercise locked periods.
