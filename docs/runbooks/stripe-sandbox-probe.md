# Stripe sandbox probe

- **Audience:** Maintainers who hold a Stripe sandbox
- **Status:** Implemented tooling; first run recorded 2026-10-08, payouts still to record
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-08

The probe asks a Stripe sandbox the questions the payment adapter depends on and records what Stripe
sends. [ADR-054](../adr/ADR-054-stripe-sandbox-adapter.md) lists the questions. The probe is a
standalone script: the application has no Stripe client, and a host configured for the Stripe
sandbox mode still refuses to start.

It cannot move real money. It refuses a key that is not a test-mode key, and it stops at the first
object Stripe marks as live.

## What you need

- A Stripe sandbox with Connect turned on, and one connected account in it that:
  - pays its own Stripe fees. A new platform has to create the account through Accounts v2, with
    Stripe collecting its fees from the account; Stripe refuses the first-generation Accounts API
    for this. The probe reads the account back and prints a warning if the fee payer is not the
    account;
  - has the card and ACH direct debit capabilities active;
  - has a test bank account to pay out to, on an automatic daily payout schedule;
  - has ACH direct debit turned on in its payment method settings. An active capability is not
    enough: without the setting a payment offers only card.
- The Stripe CLI, version 1.53 or later, signed in to that same sandbox. The probe uses it only to
  receive webhook deliveries. Without it the probe still runs and says that deliveries were not
  captured. Signed in to a different account, it delivers nothing, and the probe says so.
- Node 26.
- Two environment variables. Keep both out of the repository, issues and chat:
  - `STRIPE_SANDBOX_SECRET_KEY`: the sandbox's secret key (`sk_test_` or `rk_test_`). It must be
    the platform's key. A connected account with its own dashboard has keys too, and the probe
    stops when it is given one;
  - `STRIPE_SANDBOX_CONNECTED_ACCOUNT`: the connected account id (`acct_`).

## Run it

From the repository root:

```
node scripts/stripe-probe.mjs run
```

The run makes six test charges on the connected account: three by card and three by ACH, including
a decline, a failed debit and a disputed debit. It waits for each ACH payment to finish and for the
dispute to appear, up to ten minutes each. The first recorded run took about ten minutes; allow up
to forty.

| Option              | Effect                                                         |
| ------------------- | -------------------------------------------------------------- |
| `--only=<charge>,…` | Make only the charges named. The usage message lists the names |
| `--wait-minutes=20` | Wait longer for a payment or a dispute                         |
| `--no-listen`       | Skip webhook capture                                           |
| `--port=4242`       | The local port the Stripe CLI forwards deliveries to           |

Each line it prints is one answer. A step that fails is printed as an answer too, and the run goes
on to the next. A dropped connection is retried. The first line printed names the run's directory.
When the run ends it names the `findings.md` it wrote there. If you interrupt it, it still writes
`findings.md` there with the answers so far, but does not name it again.

## What it writes

Everything goes under `stripe-probe.local/<run>/`, which git ignores:

- one numbered file for each request and its response, and one for each webhook delivery as it
  arrives. While it waits for a payment it keeps only the last answer;
- `findings.md`, the answers in words.

The numbered files hold the sandbox's real identifiers and the accounts' details. The key and the
webhook signing secret are never written.

What is printed and what is in `findings.md` have identifiers replaced with synthetic ones. Paste
`findings.md` into issue #513 after reading it once: it quotes Stripe's own messages, and the
replacement works from patterns.

## Payouts, on a later day

Stripe pays out on its own schedule, so the first run usually finds no payout. A day or two later,
add payout evidence to the same run:

```
node scripts/stripe-probe.mjs payouts stripe-probe.local/<run>
```

It records the balance and the twenty most recent payouts and, for the three most recent automatic
ones, the lines Stripe says each covers. It needs the same two environment variables. It makes no
charge. It never creates a payout: which charges a payout covers has to come from Stripe.

## Commit the recorded payloads

```
node scripts/stripe-probe.mjs scrub stripe-probe.local/<run>
```

This copies the numbered files to `tests/fixtures/stripe/<run>/`, changed in four ways:

- every identifier is replaced with a synthetic one, the same one each time it recurs;
- an account is reduced to the few fields the adapter reads, so no business name, descriptor, time
  zone or contact detail is copied;
- fields that identify a person, a bank account or a device are removed, with URLs, email addresses
  and anything shaped like a key;
- metadata is kept only where LeaseBook wrote it.

Two checks then run on the result, and if either finds something the command writes nothing:

- the patterns the scrubber uses, applied again to what it wrote;
- every identifier and every account name, descriptor and address taken from the recorded files
  themselves, searched for as exact text. This one does not depend on what an identifier looks like.

Read the diff before committing it. Both checks know only what the run recorded and what the
patterns describe, and a person is the check on the rest. Continuous integration runs the first
check on every payload committed under `tests/fixtures/stripe/`. Recorded payloads live nowhere
else.

## If something goes wrong

Running again makes new charges, so name the ones you need with `--only`.

- **The key is refused at the first call.** The message gives the status. A 400 that names the API
  version means the version the adapter is pinned to is wrong for this account; report that on the
  issue.
- **A payment method is not accepted on the connected account.** That is a finding, not a failure.
  The run records it and goes on.
- **No dispute arrived.** Run `--only=ach-dispute` with a longer `--wait-minutes`.
- **Webhook deliveries were not captured.** Check that `stripe listen --print-secret` prints a
  secret and that the CLI is signed in to the sandbox the key belongs to. If the port is in use,
  choose another with `--port`.
- **`stripe listen` stopped as soon as it started.** Run
  `stripe listen --latest --all-snapshot --forward-to localhost:4242/account` by hand; it says why.
  The probe was run with CLI 1.53; an older one may not accept `--all-snapshot`.
- **The key belongs to the connected account.** In the dashboard, switch to the sandbox itself
  before copying the key.
- **A live-mode object was returned.** The probe stops and does not record it. The key or the
  connected account is not the sandbox's; check both before running again.
- **`scrub` refuses.** It names the file and how the value begins. Nothing was written. Report it
  on the issue; do not edit the fixture by hand.
- **Starting over.** Each run has its own directory. Delete it to discard the run; nothing else
  refers to it.
