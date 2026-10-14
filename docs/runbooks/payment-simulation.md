# Isolated payment simulation

- **Audience:** Developers and test operators
- **Status:** Implemented, non-live only
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-13

The simulator exercises the real Payments and Accounting path. It has no provider network client
and cannot move real money. Production and Staging reject Simulation; live/provider configuration
is refused. A second mode, `StripeSandbox`, collects through a Stripe sandbox instead of the
simulator, with test payment methods only.
[Run the Stripe sandbox fixture](#run-the-stripe-sandbox-fixture) takes one card payment and one ACH
payment through to a posted payout in that mode, and says which part of that has been run against a
real sandbox. [Run against the Stripe stand-in](#run-against-the-stripe-stand-in) drives the same
mode with no Stripe at all, for the browser test that continuous integration runs. Everything before
those two sections describes the simulator. The
[specification](../payments/simulated-payment-spec.md) owns the financial contract.

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
exit with code 1. `bank` is the reverse: it belongs to the Stripe sandbox fixture, described under
[A payout](#a-payout), and is refused in a simulation host.

Other event kinds are `Processing`, `Succeeded`, `Available`, `PayoutPending`, `PayoutFailed`,
`Failed`, `Return`, `Refund` and `Dispute`. A return after receipt preserves that receipt and routes
the operation to review; it never invokes an unrestricted reversal. A PMAdmin then resolves it on the
Operations page (ADR-052): **Post return** posts a full return as the receipt's linked reversal, dated
on the return evidence date, or reports the reason it cannot; **Close review…** records a note and
posts nothing, for a return staff have corrected by hand. **Post return** is offered only when the
evidence held could be posted as a return: not for a refund, a dispute, a partial return or
conflicting evidence, which can only be closed. Sign in as `admin-a@payments.test` with the
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

The stand-in fixture is a third database and manifest. `./scripts/payment-fixture.ps1 reset -StandIn`
discards and recreates it, and touches neither of the other two: the switch alone chooses which
database and manifest a command names. It also refuses a manifest that binds anything but the
stand-in's synthetic accounts.

## Run the Stripe sandbox fixture

This mode proves the processor seam against Stripe
([ADR-054](../adr/ADR-054-stripe-sandbox-adapter.md), proposed). It is for a maintainer who holds a
sandbox. It runs only in Development, only with a test-mode key, and only with Stripe's documented
test payment methods, so it cannot move real money. Continuous integration never calls Stripe.

The adapter submits a charge, finds it again, and reads Stripe's events about how it went:
processing, succeeded and failed, and afterwards a dispute or a refund. It also reads that a payout
was paid or failed. A payout is stored with Stripe's own lines and held: no Stripe event posts
anything. A payout posts only after you say what the bank received for it, with a fixture command, and
only when that amount is Stripe's.

### The run, in order

This is the whole procedure for one card payment and one ACH payment. Each step has its own part
below.

1. Check [what you need](#what-you-need).
2. [Create the sandbox fixture](#create-the-sandbox-fixture), or bring an older fixture's database
   up to date.
3. [Start the host](#start-the-host).
4. [Forward Stripe's events](#forward-stripes-events).
5. [Make a card payment and an ACH payment](#make-a-card-payment-and-an-ach-payment), and see each
   reach "Paid on … on its way to the bank".
6. Leave the account alone and [wait for Stripe's payout](#wait-for-stripes-payout). The host shows
   it as **Waiting for the bank**.
7. [Say what the bank received](#say-what-the-bank-received). The payout posts and both payments
   become **Settled**.
8. [Check the result](#check-the-result) with `check-invariants`.

Steps 1 to 5 have been run against a real sandbox, as ADR-054 records: card and ACH payments were
charged, succeeded and got their paid date, and nothing posted. The rest has not been run there for a
payout that posts. The sandbox had made one payout by 2026-10-11. The host read it and stored it with
its lines, but it also held failed and disputed test payments, so it went to review at once, and bank
evidence for it posted nothing. A payout that waits for the bank and then posts is proven in the
integration tests and against replayed payloads only, and steps 6 to 8 are written from those tests.
The screens were checked against the sandbox through the API, not in a browser. If a real run differs
from this page, believe the run and correct the page.

### What you need

- A provisioned sandbox, as the [Stripe sandbox probe runbook](stripe-sandbox-probe.md) describes: the
  sandbox's own test-mode secret key (`sk_test_` or `rk_test_`), and a connected account that pays
  its own Stripe fees and has ACH debit turned on in its payment method settings.
- The Stripe CLI, version 1.53 or later, signed in to that same sandbox. It forwards Stripe's events
  to the local host and prints the secret they are signed with. Run one Stripe CLI process at a
  time, and let one finish before you start the next: two started together have once left the CLI
  signed out of the sandbox.
- The development Postgres container running (`./scripts/dev.ps1 up`).
- A connected account that is quiet: everything charged on it before has already been paid out, and
  nothing else will be charged on it for the days the run takes. See
  [Wait for Stripe's payout](#wait-for-stripes-payout) before you start.

### Create the sandbox fixture

```powershell
./scripts/payment-fixture.ps1 init -StripeAccount <acct_…>
```

This creates the database `leasebook_payment_fixture_stripe` and writes `payment-fixture-stripe.local`
(gitignored), beside the simulation fixture and independent of it. Each account named gets one
fixture organization; name up to 26, separated by commas, each once. The first organization's
sign-ins are `tenant-a1@payments.test`, `tenant-a2@payments.test` and `admin-a@payments.test`, the
second's use `b`, and so on, with the password given above.

The manifest holds the account ids and never the key or the webhook signing secret. `init` needs
neither and makes no call to Stripe; the script clears `Payments__Stripe__SecretKey`,
`Payments__Stripe__WebhookSecret` and `Payments__Stripe__ApiBase` for the processes it starts and
puts them back afterwards.

**An older fixture.** The script applies migrations only when it creates the database. A sandbox
fixture created before 2026-10-11, when payouts were first stored, lacks the columns a held payout
needs, and a host from `main` cannot read payouts against it. Either reset it, which discards its
payments, or apply the migrations as the migrator role and keep it. Set the connection string
first; without it the command does not reach the fixture database:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Migrations = 'Host=localhost;Port=5632;Database=leasebook_payment_fixture_stripe;Username=leasebook_migrator;Password=dev_migrator_pw'
dotnet ef database update --project src/LeaseBook.Web --context AppDbContext
```

### Start the host

The host needs two secrets: the sandbox key, and the secret Stripe signs its events with. Print the
second with the Stripe CLI, in any terminal, and let the command finish before you run the CLI again:

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
  are the second kind: `--all-snapshot` forwards every event type and the host reads nine.
- `400` on every delivery means the signature did not verify. Check that the secret is the one this
  CLI sign-in prints, and that this machine's clock is right: a signature more than 300 seconds from
  the host's clock, either way, is refused.
- `429` means the callback route's rate limit, 1200 deliveries a minute from one address, was passed.

The route is `POST /callbacks/payments/stripe`. It exists only in a host in this mode, is outside
cookie-authenticated `/api`, and is not part of the published API contract. It reads at most 64 KiB.
The host checks the `Stripe-Signature` header before it reads anything in the body. It refuses an
event not marked as test mode. It routes an event by the connected account it names, to the fixture
bound to that account, and takes nothing else in the event as saying which organization it is for.

### Make a card payment and an ACH payment

**Set the fee rules first.** Stripe keeps a fee from every charge. A fee the tenant was not quoted is
a shortfall, which is taken from management fees held in the trust bank, and a fresh fixture holds
none: the payout would go to review as `pm_fees_insufficient` instead of posting. Sign in as
`admin-a@payments.test`, open **Settings**, and under **Online payment fees** set **Card** to a rate
of 2.9% and a fixed amount of $0.30, and **Bank debit (ACH)** to a rate of 0.8%. Those are the fees
the sandbox kept in the runs ADR-054 records. With them, the fee quoted for the amounts below is the
fee Stripe kept there, so nothing is left over either way.

Then, as `tenant-a1@payments.test` in the tenant portal:

1. The panel is headed **Test payments**, with the badge "Test payment — no real money moves". If it
   says "Simulated payments", the host is not in this mode.
2. Enter `500.00`, leave **Pay with** on **Bank debit (ACH)**, check the fee shown ($4.03, for a
   total of $504.03) and choose **Submit test payment**.
3. Enter `500.00` again, choose **Card**, check the fee ($15.24, for a total of $515.24) and submit.
   Each submission is a payment of its own.

What each payment then says:

| When                                       | The tenant's row reads                                                   |
| ------------------------------------------ | ------------------------------------------------------------------------ |
| Submitted, not yet sent                    | Test payment requested                                                   |
| Stripe has it and has not said how it went | Test payment processing — not yet on your ledger                         |
| Stripe says it succeeded                   | Paid on _date_ — on its way to the bank, not yet on your ledger          |
| Stripe says it failed                      | Test payment failed — no payment recorded                                |
| It needs a person                          | Test payment needs review — no receipt recorded, and a sentence below it |
| A payout that holds it has posted (step 7) | Test payment recorded                                                    |

The card payment succeeds within a second or two. The ACH payment stays in processing for about half
a minute. Once both have succeeded the panel also says "In transit: $1,000.00", and the tenant's
balance has not moved: no journal entry has been written. On **Operations**, staff see the same two
payments under **Test payments**, with the organization's total in transit.

If a payment failed or went to review, [What to expect](#what-to-expect) says why. A failed ACH
debit, a dispute or a refund lands in the same payout as the good payments and keeps all of it from
posting. Resetting the fixture does not help, because Stripe still pays out what the account already
holds. Wait for that payout to be made, which will go to review, and then make the two payments
again.

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
  Stripe for the payment and its charge. A PMAdmin can close the review. A dispute event never
  posts a return, on a payment with a receipt included: Stripe's word about a dispute is not bank
  evidence, so **Post return** is not offered on such a payment and the route behind it refuses.
  A return posts only inside a payout
  that an administrator posts.
- `payout.paid` and `payout.failed` are read as [A payout](#a-payout) describes. Every other event
  type is acknowledged and dropped, Stripe's other four payout events included.
- A `503` on a dispute's delivery means Stripe could not be asked about it just then. So does one on
  a `payout.paid` delivery: the payout or its lines could not be read in full, or Stripe has not
  finished reconciling the payout. The host logs event `4608`, and the sweep reads the event again
  within about five minutes. A `500` is a fault in the host: see the
  [diagnostics runbook](diagnostics.md#online-payment-processing).
- A payment that succeeds gets its paid date and stays **Processing**. No receipt is recorded and no
  journal entry is written: a Stripe event is never bank evidence. It is receipted, and becomes
  **Settled**, only when a payout that holds it posts.
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
- `payment-simulation step` works with the same five variables and repeats the key check, and so
  does `bank`. `emit` and `payout` are refused. The simulator's callback routes do not exist in this
  host.

### A payout

Stripe pays a connected account out on its own schedule. LeaseBook never makes a payout and never
works out for itself what one covers.

#### Wait for Stripe's payout

**Keep the account quiet while a payout forms.** Stripe decides what a payout covers, and other
activity on the same connected account can land in the same payout. From the first of your two
payments until the payout is posted, do not run the [probe](stripe-sandbox-probe.md) against the same
account and do not reset the fixture. The probe's charges, and those of a generation a reset
replaced, land in the same payout as lines LeaseBook cannot tie to a payment, and the whole payout
goes to review instead of posting. The same goes for a failing or disputed test payment made in that
window. A payout holding only this generation's good payments is the one that posts.

**How long.** Nobody here can say exactly. The connected account's schedule reads daily with a
two-day delay. The one automatic payout recorded so far was created under three hours after the
first charges, with the same day as its arrival date, and was marked paid five minutes after it was
created. No second payout had been made two days after that. Expect to wait between a few hours and
a few days. The host can be stopped in the meantime: when it starts again it asks Stripe for the last
29 days of events and finds the payout. Start it with the same key and signing secret, and start
`stripe listen` again only after the host is up.

**What to look for.** As `admin-a@payments.test`, open **Operations** and find the **Payouts** list.
Before any payout it reads "No payouts yet. A payout appears here when the processor reports it."
When Stripe has paid, the payout appears as **Payout po\_…** with the status **Waiting for the bank**,
the words "No bank evidence has arrived for this payout. Nothing posts until the bank says what it
received.", and "Processor’s payout amount: $1,000.00". Its two lines are of the kind **Payment**,
each "Not on the ledger". There is no **Post payout** button: an administrator cannot post it. Note
the payout's id and the processor's amount; the next step needs both. A payout that shows **Needs
review — nothing posted** instead will not post: read why under [In review](#in-review).

**What the host reads.** A `payout.paid` or `payout.failed` event, delivered or found by the sweep.
For a paid payout the host asks Stripe for the payout's balance lines, every page, and for the charge
behind each dispute among them. If the event does not say the payout's reconciliation is `completed`,
it first asks Stripe for the payout as it is now, and reads no lines until Stripe says so. Each
request carries an identifier and nothing else. The payout is stored with every line Stripe listed
and held. It shows in the **Payouts** list on Operations as **Waiting for the bank**, with the
processor's payout amount and the words "No bank evidence has arrived for this payout"; no bank
amount or date is shown until there is bank evidence. No journal entry is written.

A payout belongs to the fixture bound to its account, whichever generation's payments it holds, so a
payout of the probe's charges or of an earlier fixture is stored too.

**How a line is read.**

| Stripe's balance line                                     | Stored as                                             | Can it post?                                |
| --------------------------------------------------------- | ----------------------------------------------------- | ------------------------------------------- |
| `charge` (card) or `payment` (ACH) for a payment          | Payment                                               | Yes                                         |
| `adjustment` for a dispute on an ACH debit                | Return, and a Fee beside it for the dispute's own fee | Only when an administrator posts the payout |
| `adjustment` for any other dispute                        | Dispute                                               | No. The whole payout goes to review         |
| `refund` or `payment_refund`                              | Refund                                                | No. The whole payout goes to review         |
| `payment_failure_refund` (a failed ACH debit, taken back) | FailedDebit                                           | No. The whole payout goes to review         |
| Anything else                                             | Unknown                                               | No. The whole payout goes to review         |
| The payout's own line                                     | Not a line: it is Stripe's net for the payout         | Not applicable                              |

A payout is checked once when it arrives, without the bank. One that could never post goes to review
then, as `unsupported_settlement` for a line in the last four rows or for an instant payout,
`settlement_incomplete` for a line whose charge is not a payment of this fixture generation, and so
on. A manual payout is stored without lines, is not asked about, and goes to review as unsupported.
Any other payout waits for the bank.

#### Say what the bank received

Stripe never says this, and no HTTP route or staff control does. For the run above, the amount is the
processor's payout amount the list shows, and the date is today's or the payout's arrival date. In a
terminal with the same five variables as the host:

```powershell
$fixture = (Get-Content ./payment-fixture-stripe.local -Raw | ConvertFrom-Json).Payments.Fixtures[0]
dotnet run --project src/LeaseBook.Web --no-launch-profile -- payment-simulation bank $fixture.OrgId <po_…> <bank-amount> <yyyy-MM-dd>
```

The amount is in dollars with at most two decimal places, negative for a payout that debited the
bank. The date is the day the bank received it and may not be after today in UTC. The command signs
the evidence with the fixture's signing key, checks and posts in its own process, and prints the
payout's status and reason. It exits with code 1 when it refuses what it was given.

| What you gave                                          | What happens                                                                                                                                           |
| ------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Stripe's net, for a payout that was only waiting       | Every line posts, or none. The payout is **Posted** and its payments **Settled**. A refused posting sends it to review under that reason               |
| Stripe's net, for a waiting payout that holds a return | Review, as `settlement_requires_confirmation`. An administrator posts it from the **Payouts** list                                                     |
| Any other amount, for a payout that was only waiting   | Review, as `bank_amount_mismatch`. Nothing posts and it can only be closed                                                                             |
| Any amount, for a payout already in review             | The evidence is recorded and nothing is checked again. An administrator's **Post payout** checks it then                                               |
| The same amount and date a second time                 | Nothing changes                                                                                                                                        |
| A different amount or date after one was recorded      | Not recorded, exit code 1. A payout still awaiting a decision is marked `conflicting_evidence` and can only be closed, a later correct amount included |
| A date after today                                     | Refused before anything is sent                                                                                                                        |
| A payout id this fixture does not hold                 | Refused: `no Stripe payout … is held in this fixture`                                                                                                  |

Give the evidence once and get it right: there is no command that corrects it.

#### Check the result

For the run above, with the fee rules set as described, the command prints that the payout is
`Posted`, and:

- The **Payouts** list shows the payout as **Posted**, with "$1,000.00 at the bank on" the date you
  gave, and each line "On the ledger".
- Each payment reads **Test payment recorded** for the tenant, and the tenant's balance is down by
  $1,000.00. Nothing is in transit any more.
- The journal has two new entries, one receipt of $500.00 for each payment, dated on the bank date
  and described "Stripe sandbox tenant payment". Stripe's fees are not in the journal: the tenant
  paid them on top, as the quoted convenience fee.
- Had Stripe kept more or less than the quoted fee, each difference would be one more entry, against
  the management fees held in the trust bank and with no tenant, owner or property on it.

Then check the invariants, in a terminal with the same five variables as the host:

```powershell
dotnet run --project src/LeaseBook.Web --no-launch-profile -- check-invariants --all
```

It should print `all clean` for the fixture's organizations. To see the payout reconcile, import a
statement on **Banking** as [Drive a payout](#drive-a-payout) describes for the simulator: one line
for the bank amount on the bank date matches the payout as a group.

#### In review

An administrator can close a payout in review or one still waiting for its bank, with
a note. Posting one that no bank has spoken for is answered with HTTP 409 and `bank_evidence_missing`.
A payout in review is checked again only when an administrator posts it: the paid event arriving
again, by redelivery or in a sweep, changes nothing. The **Payouts** list says in words why a payout
waits, `bank_amount_mismatch` and `payout_failed` included, and names each line's kind: **Card
dispute**, **Refund**, **Failed bank debit**, or **Unrecognised line**. It shows Stripe's figure as
the processor's payout amount and, for a failed payout, when Stripe said it failed.

A payment in review for a bank's return of an ACH debit is best left open until the payout that
carries the return has been dealt with. The return posts only as a line of that payout, and a payout
refuses a return for a payment whose review was closed. A payout that holds both a payment and that
same payment's return cannot post at all; [ADR-054](../adr/ADR-054-stripe-sandbox-adapter.md#known-limits)
lists this with the other known limits.

#### A payout that fails

`payout.failed` sends the payout to review as `payout_failed`, whatever state it was in, one already
posted included. Nothing is reversed: what it posted stays posted, its payments stay as they were,
and it can only be closed. For one that had posted, the list then reads **Needs review — posted,
nothing reversed**. Correct the ledger by hand if the fixture needs it.

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

| The host says                                                                     | What it means                                                                                                                                                                                                 |
| --------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Unknown setting: Payments:Stripe:SecretKey`                                      | The key is set for a host that is not in this mode. Remove the variable, or point `Payments__ManifestPath` at the sandbox manifest.                                                                           |
| `Unknown setting: Payments:Stripe:WebhookSecret`                                  | The same, for the webhook signing secret.                                                                                                                                                                     |
| `A payment fixture manifest must not hold provider credentials`                   | The key or the signing secret was written into the manifest. Take it out, and rotate a key that was there.                                                                                                    |
| `require Development, a fixture signing key and unique explicit fixture bindings` | The environment is not Development, or the manifest is not this mode's: every account must be `acct_…`, each once.                                                                                            |
| `StripeSandbox requires a Stripe test-mode secret key`                            | The key is missing, or does not begin `sk_test_` or `rk_test_`. A live key is refused here, before any call is made.                                                                                          |
| `StripeSandbox requires a Stripe webhook signing secret (whsec_…)`                | `Payments:Stripe:WebhookSecret` is missing, does not begin `whsec_`, or has a space or line break in it. Supply the value `stripe listen --print-secret` prints.                                              |
| `Payments:Stripe:ApiBase must be a local stand-in's address and nothing else`     | The setting points the host at a local stand-in for Stripe, for browser tests (ADR-054). It takes `http`, then `127.0.0.1`, `localhost` or `[::1]`, then a port. To talk to the sandbox, remove the variable. |
| `Payments:Stripe:ApiBase is admitted only with the stand-in's own secret key`     | A stand-in address is set beside a sandbox key. Remove the address to talk to the sandbox. A key of Stripe's is never sent to a stand-in.                                                                     |
| `The stand-in's secret key … is admitted only with a stand-in's address`          | The key is the stand-in's and no address is set. Supply the sandbox's own key, or the stand-in's address.                                                                                                     |
| `collects only with Stripe's documented test payment methods`                     | An optional payment method does not have the test shape. The message names the setting.                                                                                                                       |
| `could not read the account of its Stripe key`                                    | Stripe could not be reached, or it refused the key: revoked, mistyped, or a restricted key that may not read accounts.                                                                                        |
| `requires the platform's test-mode key, not a connected account's own`            | The key belongs to a fixture's connected account. An account with its own dashboard has its own keys; copy the key from the sandbox itself.                                                                   |
| `fixture organization <id> is not bound to a test connected account … fees`       | That fixture's account cannot be read with this key, or does not pay its own Stripe fees. Check the id, and that the account belongs to this sandbox and was created so.                                      |

## Run against the Stripe stand-in

`scripts/stripe-stand-in.mjs` is a local stand-in for Stripe
([ADR-054](../adr/ADR-054-stripe-sandbox-adapter.md), the note of 2026-10-13). A host in
`StripeSandbox` mode is pointed at it with `Payments:Stripe:ApiBase`, and then makes no call to
Stripe. It needs no sandbox, no key of Stripe's and no Stripe CLI. It exists for one purpose: to
drive the sandbox screens through a browser, in continuous integration and on a developer's
machine. It proves nothing about Stripe itself; the recorded payloads it answers from go stale
silently if Stripe changes a shape.

The stand-in listens on loopback only and sends deliveries to loopback only. Every request but its
health check must carry the fixed key `sk_test_leasebook_stand_in`, which Stripe issues to nobody
and which the host refuses without a stand-in address. It keeps what it is told in memory and
forgets it when it stops.

### Create the stand-in fixture

```powershell
./scripts/dev.ps1 up
./scripts/payment-fixture.ps1 init -StandIn
```

This creates the database `leasebook_payment_fixture_standin` and writes
`payment-fixture-standin.local` (gitignored), beside the other two fixtures and independent of them.
It is a sandbox fixture bound to two synthetic connected accounts, `acct_standin000001` and
`acct_standin000002`, with one organization each. The sign-ins are those of
[the sandbox fixture](#create-the-sandbox-fixture): `tenant-a1@payments.test`,
`admin-a@payments.test`, and `b` for the second organization. `-StandIn` and `-StripeAccount` cannot
be given together.

### Start the stand-in and the host

Both read the same variables, so set them once in a terminal kept for this and start each from it.
The signing secret is made here and not kept: any value of the shape `whsec_…` that both hold will
do, and the browser test makes its own for each run.

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Default = 'Host=localhost;Port=5632;Database=leasebook_payment_fixture_standin;Username=leasebook_app;Password=dev_app_pw'
$env:Payments__ManifestPath = (Resolve-Path ./payment-fixture-standin.local).Path
$env:Payments__Stripe__ApiBase = 'http://127.0.0.1:4010'
$env:Payments__Stripe__SecretKey = 'sk_test_leasebook_stand_in'
$env:Payments__Stripe__WebhookSecret = 'whsec_' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
node scripts/stripe-stand-in.mjs
```

Start the stand-in first: the host asks whose key it holds before it serves, and does not start
without an answer. Then, in a second terminal with the same six variables:

```powershell
dotnet run --project src/LeaseBook.Web --no-launch-profile --urls http://localhost:5080
```

The stand-in listens where `Payments__Stripe__ApiBase` says, holds the connected accounts the
manifest binds, and signs with the secret in `Payments__Stripe__WebhookSecret`. It sends deliveries
to `http://127.0.0.1:5080/callbacks/payments/stripe`; `--callback=<url>` names another loopback
address. It prints one line for each request it answers and each delivery it sends, and never a
header, a key or the secret.

**A proxy in the environment.** The host's HTTP client takes a proxy from `http_proxy`, `HTTP_PROXY`
or `ALL_PROXY`, for a loopback address too. With one set, the host's requests to the stand-in go to
the proxy and the host refuses to start, saying it could not read the account of its Stripe key. Set
`NO_PROXY` to `127.0.0.1,localhost` in the host's terminal. The browser test sets it for itself.

### What the stand-in answers

It answers the requests the adapter makes and no others. A request it does not know is answered
with an error whose code is `stand_in_unknown_request`, printed as `UNEXPECTED`, and kept on a list
the browser test reads and requires to be empty.

Its answers come from the payloads recorded from a real sandbox under `tests/fixtures/stripe/`. No
recorded payload is changed and none is added. A recorded shape is copied and given this run's
values: identifiers, amounts, times, the connected account, and the two ids of LeaseBook's that a
charge carries.

| The host asks, or Stripe sends              | The stand-in's answer                                        | Recorded or built                                                                                                                          |
| ------------------------------------------- | ------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------ |
| `GET /v1/account`                           | The platform's account                                       | Recorded, unchanged                                                                                                                        |
| `GET /v1/accounts/{id}`                     | A fixture's connected account, which pays its own fees       | Recorded shape; the id is substituted                                                                                                      |
| `POST /v1/payment_intents`, `pm_card_visa`  | A card payment that has succeeded                            | Recorded shape; ids, amount, time and metadata are substituted                                                                             |
| The same idempotency key and parameters     | The same payment                                             | The behaviour was recorded; the answer is the payment the stand-in holds                                                                   |
| The same idempotency key, another parameter | HTTP 400, `idempotency_error`                                | Recorded; the key in the message is substituted                                                                                            |
| `GET /v1/payment_intents/{id}` and the list | The payment as it was created; a list of them                | The payment is the recorded shape. Retrieving one was not recorded for a card payment. The list's envelope is recorded                     |
| `GET /v1/charges/{id}`                      | The payment's charge                                         | Recorded shape: the charge inside the recorded `charge.updated` event, substituted                                                         |
| The four events of a card payment           | `charge.succeeded`, `payment_intent.succeeded`, and two more | Recorded deliveries, in the order Stripe sent them, substituted                                                                            |
| The four events of a payout                 | `payout.created` through `payout.updated`                    | Recorded shapes, substituted. They were recorded from Stripe's list of events and not as deliveries                                        |
| `GET /v1/balance_transactions?payout=…`     | The payout's own line and one line for each payment          | Recorded shapes: the recorded payout's own line and its line for a card charge, substituted. The charge inside a line is the payment's own |
| `GET /v1/events`                            | The events the stand-in has made, newest first, in pages     | The envelope is recorded; the events are the ones above                                                                                    |
| `GET /v1/payouts/{id}`                      | The payout as its last event left it                         | Built from the recorded payout: retrieving one was not recorded                                                                            |
| An id it does not hold, on a route it knows | HTTP 404, `resource_missing`                                 | Built: no such answer was recorded                                                                                                         |

Three things are built and not recorded, and a test that passes against them shows less than one
against a sandbox would:

- **A payout that holds only good payments.** The sandbox has made none
  ([ADR-054](../adr/ADR-054-stripe-sandbox-adapter.md#known-limits), "Not yet shown"). The stand-in
  builds one from the recorded payout's shapes: its amount is the sum of its payments' nets, and
  its lines come to nothing against it, as the recorded payout's do.
- **The fee on a charge.** No recording holds a charge of the amount a test pays. The stand-in
  works it out at the rates the sandbox kept in the recorded runs: 2.9% plus $0.30 for a card, to
  the nearest cent. For the recorded card charges of $515.24 and $200.00 it gives the fees Stripe
  kept, $15.24 and $6.10.
- **When things happen.** Stripe sends a card payment's events within seconds and makes a payout
  on its own schedule. The stand-in sends each when a test tells it to.

A payment method other than `pm_card_visa` has no recorded timeline here yet and is refused as an
unknown request.

### Tell the stand-in what happens next

Stripe acts by itself; the stand-in acts when told. Its control surface is under `/stand-in/`, which
no route of Stripe's begins with, and takes the same key in `Authorization: Bearer …`.

| Request                                                | What it does                                                                                                                                                                        |
| ------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GET /stand-in/health`                                 | Answers `{"ok":true}` with no key. Says nothing else                                                                                                                                |
| `GET /stand-in/state`                                  | The accounts, payments and payouts it holds, the deliveries it sent with the host's answers, and the list of unexpected requests                                                    |
| `POST /stand-in/payments/{payment reference}/timeline` | Body `{"timeline":"card-succeeds"}`. Sends the recorded events of that timeline for the payment LeaseBook made under that reference, and answers with each delivery's HTTP status   |
| `POST /stand-in/payouts`                               | Body `{"payments":["<payment reference>", …]}`. Forms one automatic payout from payments Stripe has said were paid, sends its four events, and answers with the payout's id and net |

Either `POST` takes `"deliver": false`, which makes the events without sending them, as an event
Stripe never delivered: `GET /v1/events` then lists them for the host's catch-up sweep. No browser
test drives that yet. Deliveries are real signed requests to the host's callback route, and the
stand-in does not send one again.

To take a payment through by hand: pay by card in the tenant portal as
[above](#make-a-card-payment-and-an-ach-payment), after setting the card fee rule; copy the payment
reference; then, in a terminal:

```powershell
$key = @{ Authorization = 'Bearer sk_test_leasebook_stand_in' }
$base = 'http://127.0.0.1:4010/stand-in'
Invoke-RestMethod "$base/payments/<payment reference>/timeline" -Method Post -Headers $key -ContentType 'application/json' -Body '{"timeline":"card-succeeds"}'
$payout = Invoke-RestMethod "$base/payouts" -Method Post -Headers $key -ContentType 'application/json' -Body '{"payments":["<payment reference>"]}'
$payout.id; $payout.net
```

The payout then shows on **Operations** as **Waiting for the bank**.
[Say what the bank received](#say-what-the-bank-received) with the payout's id and its net, using
`payment-fixture-standin.local` for the manifest and the same six variables.

### Discard it

Stop the host and the stand-in. `./scripts/payment-fixture.ps1 reset -StandIn` discards the fixture
and makes a new one. To remove it altogether, drop the database
`leasebook_payment_fixture_standin` and delete `payment-fixture-standin.local`. Neither command
touches the simulation fixture or a sandbox fixture.

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
post a payout that contains a return from the keyboard. Two more tests drive the second fixture
organization, so that they share no payment, payout or fee rule with the first two. In one, a
receipted payment is disputed and its administrator closes the review with a note. In the other, a
payout arrives that does not add up to the bank amount, and the administrator closes it with a note
and nothing posts. Both close from the keyboard. Backend integration tests additionally inject failures around
provider acceptance and ledger commit, validate routing/isolation and exercise locked periods.

The Stripe sandbox mode has a browser test of its own, against
[the stand-in](#run-against-the-stripe-stand-in). Create the stand-in fixture, stop any running host
and stand-in, then from `web/`:

```powershell
$env:STRIPE_STAND_IN_E2E = '1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Default = 'Host=localhost;Port=5632;Database=leasebook_payment_fixture_standin;Username=leasebook_app;Password=dev_app_pw'
$env:Payments__ManifestPath = (Resolve-Path ../payment-fixture-standin.local).Path
npx playwright test stripe-stand-in.spec.ts
```

Playwright starts the stand-in, then the host, then the SPA, and stops them afterwards. It gives the
host the stand-in's address and key, and a signing secret it makes for the run. CI runs this in a
job of its own, `E2E (Stripe stand-in)`, beside the ordinary e2e job; in that job Stripe's hosts
resolve to nothing, so a request to Stripe would fail and not be made quietly.

The test drives one scenario so far: a card payment that reaches a posted payout. The tenant pays in
the portal under the words "Test payment — no real money moves". The stand-in says the payment
succeeded, and the tenant sees it paid and not yet on the ledger. The stand-in pays out, and an
administrator sees the payout **Waiting for the bank** with the processor's amount and nothing to
post. The test then runs the `bank` command with the processor's net, the payout shows **Posted**,
and the tenant's payment is recorded and the balance is down by the amount. It checks WCAG 2 AA at
each of those states, runs `check-invariants --all` after the scenario, and requires that the
stand-in was asked nothing it does not know. The fixture is not reset between runs, and the test
can be run again against the same database.
