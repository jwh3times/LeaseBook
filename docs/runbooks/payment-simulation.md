# Isolated payment simulation

- **Audience:** Developers and test operators
- **Status:** Implemented, non-live only
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-09

The simulator exercises the real Payments and Accounting path. It has no provider network client
and cannot move real money. Production and Staging reject Simulation; live/provider configuration
is refused. A second mode, `StripeSandbox`, collects through a Stripe sandbox instead of the
simulator, with test payment methods only; it is partly built, and
[Run the Stripe sandbox fixture](#run-the-stripe-sandbox-fixture) says what works. Everything before
that section describes the simulator. The [specification](../payments/simulated-payment-spec.md) owns
the financial contract.

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
`emit` and `payout` drive the simulator: against a host in any other mode they print a refusal and
exit with code 1.

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
cookie cannot select the callback's organization. Both routes exist only in a Simulation host; a
Stripe sandbox host has its own callback route instead, described below.

## Restart, reset and failure recovery

An ordinary restart uses the same database and manifest. Provider acceptance, operations, inbox,
effect links and retry leases survive; leases expire after 30 seconds. Changing manifest bindings
cannot redirect existing operations. An unknown provider result keeps its original identity.

A fixture left idle ages. A payment accepted and still without a **Succeeded** or **Failed** fact
seven days after it was created goes to review with the reason `outcome_overdue` the next time a host
runs. It does not return to **Processing**: a bank credit emitted for it afterwards posts nothing, and
a payout that names it is held. Close the review, or reset the fixture.

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

The Stripe sandbox fixture is a separate database and manifest, so the command above never touches
it. Its reset needs the accounts named again, because it recreates the fixture from them:
`./scripts/payment-fixture.ps1 reset -StripeAccount <acct_…>`. The same checks apply, with the
organization count taken from that manifest. Resetting does nothing at Stripe: payments already made
in the sandbox stay there, and the new generation does not match them.

## Run the Stripe sandbox fixture

This mode proves the processor seam against Stripe
([ADR-054](../adr/ADR-054-stripe-sandbox-adapter.md), proposed). It is for a maintainer who holds a
sandbox. It runs only in Development, only with a test-mode key, and only with Stripe's documented
test payment methods, so it cannot move real money. Continuous integration never calls Stripe.

Today the adapter submits a charge, finds it again, and reads Stripe's events about how it went:
processing, succeeded and failed, and afterwards a dispute or a refund. It does not read payouts yet,
so a payment can be seen to succeed, fail or go to review, and nothing posts to the ledger.

### What you need

- A provisioned sandbox, as the [Stripe sandbox probe runbook](stripe-sandbox-probe.md) describes: the
  sandbox's own test-mode secret key (`sk_test_` or `rk_test_`), and a connected account that pays
  its own Stripe fees and has ACH debit turned on in its payment method settings.
- The Stripe CLI, version 1.53 or later, signed in to that same sandbox. It forwards Stripe's events
  to the local host and prints the secret they are signed with.
- The development Postgres container running (`./scripts/dev.ps1 up`).

### Create the fixture

```powershell
./scripts/payment-fixture.ps1 init -StripeAccount <acct_…>
```

This creates the database `leasebook_payment_fixture_stripe` and writes `payment-fixture-stripe.local`
(gitignored), beside the simulation fixture and independent of it. Each account named gets one
fixture organization; name up to 26, separated by commas, each once. The first organization's
sign-ins are `tenant-a1@payments.test`, `tenant-a2@payments.test` and `admin-a@payments.test`, the
second's use `b`, and so on, with the password given above.

The manifest holds the account ids and never the key or the webhook signing secret. `init` needs
neither and makes no call to Stripe; the script clears `Payments__Stripe__SecretKey` and
`Payments__Stripe__WebhookSecret` for the processes it starts and puts them back afterwards.

### Start the host

The host needs two secrets: the sandbox key, and the secret Stripe signs its events with. Print the
second with the Stripe CLI, in any terminal:

```powershell
stripe listen --print-secret
```

It prints one value beginning `whsec_`. Treat it like the key: keep it out of the repository, issues
and chat.

Use a terminal kept for this host. The key and the signing secret are each refused as an unknown
setting by a host in any other mode, payments disabled included, so do not put either in a profile or
in a terminal you use for anything else. Setting them as below supplies them to that terminal's
processes only.

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Default = 'Host=localhost;Port=5632;Database=leasebook_payment_fixture_stripe;Username=leasebook_app;Password=dev_app_pw'
$env:Payments__ManifestPath = (Resolve-Path ./payment-fixture-stripe.local).Path
$env:Payments__Stripe__SecretKey = Read-Host -MaskInput 'Sandbox secret key'
$env:Payments__Stripe__WebhookSecret = Read-Host -MaskInput 'Webhook signing secret'
dotnet run --project src/LeaseBook.Web --no-launch-profile --urls http://localhost:5080
```

Both are required. A host that ran in this mode before the signing secret existed does not start
until it is supplied. The secret must be the whole value and nothing else: a space or a line break
pasted with it is refused at startup.

At startup the host asks Stripe whose key it holds and reads each fixture's account. It then starts
the same worker the simulator uses. Start `npm run dev` from `web/` and sign in as a fixture tenant.

### Forward Stripe's events

In another terminal, once the host is up, forward the sandbox's events to it and leave this running:

```powershell
stripe listen --latest --all-snapshot --forward-to localhost:5080/callbacks/payments/stripe --forward-connect-to localhost:5080/callbacks/payments/stripe
```

`--forward-connect-to` is the one that matters: every event about a fixture's payment is an event on
its connected account. The CLI prints each delivery and the host's answer.

- `204` means the event was authentic. It was either stored or was not this host's to act on. Most
  are the second kind: `--all-snapshot` forwards every event type and the host reads seven.
- `400` on every delivery means the signature did not verify. Check that the secret is the one this
  CLI sign-in prints, and that this machine's clock is right: a signature more than 300 seconds from
  the host's clock, either way, is refused.
- `429` means the callback route's rate limit, 1200 deliveries a minute from one address, was passed.

The route is `POST /callbacks/payments/stripe`. It exists only in a host in this mode, is outside
cookie-authenticated `/api`, and is not part of the published API contract. It reads at most 64 KiB.
The host checks the `Stripe-Signature` header before it reads anything in the body. It refuses an
event not marked as test mode. It routes an event by the connected account it names, to the fixture
bound to that account, and takes nothing else in the event as saying which organization it is for.

### What to expect

- A submitted payment is charged on the connected account as a direct charge, for the ledger amount
  plus any quoted fee. Stripe receives the amount in cents, `usd`, a test payment method, and the
  operation id and fixture generation as metadata. Nothing identifying a tenant is sent and no
  customer is created.
- With events forwarded, the host reads `payment_intent.processing`, `payment_intent.succeeded` and
  `payment_intent.payment_failed` as the **Processing**, **Succeeded** and **Failed** facts the
  simulator also uses.
- A dispute (`charge.dispute.created`, `charge.dispute.funds_withdrawn`, `charge.dispute.closed`)
  sends the payment to review with the reason `return_requires_review`: as a **Return** when the
  payment was an ACH debit, as a **Dispute** when it was a card. So does a refund
  (`charge.refunded`), as a **Refund**. To learn whose payment a dispute is about, the host asks
  Stripe for the payment and its charge. A PMAdmin can close the review. **Post return** is not
  offered: nothing has a receipt before payouts are read, and Stripe's word about a dispute is not
  bank evidence. Every other event type is acknowledged and dropped.
- A `500` on a dispute's delivery means Stripe could not be asked about it just then. The sweep reads
  the event again within about five minutes.
- A payment that succeeds gets its paid date and stays **Processing**. No receipt is recorded and no
  journal entry is written: a Stripe event is never bank evidence, and payouts are not read yet.
- A payment that fails ends as **Failed**, with the reason `collection_failed`. This covers a
  declined card and an ACH debit that fails.
- A payment counts only when it carries this fixture's generation. The same connected account may
  hold payments made by the probe or by an earlier fixture, before a reset; their events are
  acknowledged and dropped.
- Without events forwarded, a payment's outcome shows within about five minutes instead of at once.
  See [A lost event](#a-lost-event) below.
- A payment Stripe leaves waiting on a payer, a confirmation or a capture, or reports cancelled, goes
  to review on Operations. So does one Stripe will not take however often it is sent.
- A failed connection, a rate limit, a fault at Stripe, or a key or permission Stripe refuses is a
  technical failure: it is retried on the delays above, and after those a PMAdmin can retry it.
- A payment is looked up before it is ever submitted, by listing the account's payments since the
  operation was created. Once LeaseBook has stored Stripe's reference for a payment, it retrieves
  that one payment instead. If Stripe then has no payment under a stored reference, the payment goes
  to review. Restarting the host or retrying never charges twice. An operation more than 23 hours old
  is not submitted at all; it goes to review.
- `payment-simulation step` works with the same five variables and repeats the key check. `emit` and
  `payout` are refused. The simulator's callback routes do not exist in this host.

### A lost event

The Stripe CLI's forwarding does not send a delivery again, whether the host was down, refused it or
rate-limited it. The worker recovers such an event itself. Every five minutes it asks Stripe for the
account's events since its last complete sweep, less ten minutes, and reads them as if they had been
delivered. A restarted host asks at once, for the last 29 days, so its first sweep can take a while on
an account with a long history. So a payment whose outcome, dispute or refund was missed shows it
within about five minutes, with or without `stripe listen`. The log shows each sweep as event `4606`,
and a sweep that failed as `4607`.

A payment still without an outcome seven days after it was created goes to review on Operations with
the reason `outcome_overdue`. Check it at Stripe, then close the review with a note. If its outcome
arrives later, a failure ends it as **Failed** and a success sets its paid date and leaves it in
review; it does not go back to **Processing**.

`payment-simulation step` neither sweeps nor ages; only a running host does.

### Optional payment methods

A fixture in the manifest may name the test payment method it collects with:

```json
{
  "OrgId": "…",
  "Generation": "…",
  "BankId": "…",
  "Account": "acct_…",
  "CardPaymentMethod": "pm_card_visa",
  "AchPaymentMethod": "pm_usBankAccount_success"
}
```

The values shown are the defaults, and `init` does not write them. A card method must be `pm_card_`
and an ACH method `pm_usBankAccount_`, each followed by letters, digits or underscores: the shape of
Stripe's documented test payment methods. A saved payment method's id does not have that shape and is
refused. Both settings are refused outside this mode.

To see a payment fail, stop the host, name one of Stripe's failing test payment methods here, such as
`pm_card_visa_chargeDeclined` for a card or `pm_usBankAccount_insufficientFunds` for ACH, and start
the host again. With events forwarded, a payment made with it ends as **Failed**.

### Startup refusals

Each names a setting by its path or a fixture by its organization id, never a key or a secret.

| The host says                                                                     | What it means                                                                                                                                                            |
| --------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Unknown setting: Payments:Stripe:SecretKey`                                      | The key is set for a host that is not in this mode. Remove the variable, or point `Payments__ManifestPath` at the sandbox manifest.                                      |
| `Unknown setting: Payments:Stripe:WebhookSecret`                                  | The same, for the webhook signing secret.                                                                                                                                |
| `A payment fixture manifest must not hold provider credentials`                   | The key or the signing secret was written into the manifest. Take it out, and rotate a key that was there.                                                               |
| `require Development, a fixture signing key and unique explicit fixture bindings` | The environment is not Development, or the manifest is not this mode's: every account must be `acct_…`, each once.                                                       |
| `StripeSandbox requires a Stripe test-mode secret key`                            | The key is missing, or does not begin `sk_test_` or `rk_test_`. A live key is refused here, before any call is made.                                                     |
| `StripeSandbox requires a Stripe webhook signing secret (whsec_…)`                | `Payments:Stripe:WebhookSecret` is missing, does not begin `whsec_`, or has a space or line break in it. Supply the value `stripe listen --print-secret` prints.         |
| `collects only with Stripe's documented test payment methods`                     | An optional payment method does not have the test shape. The message names the setting.                                                                                  |
| `could not read the account of its Stripe key`                                    | Stripe could not be reached, or it refused the key: revoked, mistyped, or a restricted key that may not read accounts.                                                   |
| `requires the platform's test-mode key, not a connected account's own`            | The key belongs to a fixture's connected account. An account with its own dashboard has its own keys; copy the key from the sandbox itself.                              |
| `fixture organization <id> is not bound to a test connected account … fees`       | That fixture's account cannot be read with this key, or does not pay its own Stripe fees. Check the id, and that the account belongs to this sandbox and was created so. |

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
