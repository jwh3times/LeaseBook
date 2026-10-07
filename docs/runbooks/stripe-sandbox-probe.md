# Stripe sandbox probe

- **Audience:** Maintainers who hold a Stripe sandbox
- **Status:** Implemented tooling; no run has been recorded yet
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-07

The probe asks a Stripe sandbox the questions the payment adapter depends on and records what Stripe
sends. [ADR-054](../adr/ADR-054-stripe-sandbox-adapter.md) lists the questions. The probe is a
standalone script: the application has no Stripe client, and a host configured for the Stripe
sandbox mode still refuses to start.

It cannot move real money. It refuses a key that is not a test-mode key, and it stops at the first
object Stripe marks as live.

## What you need

- A Stripe sandbox with Connect turned on, and one connected account in it that:
  - pays its own Stripe fees;
  - has the card and ACH direct debit capabilities active;
  - has a test bank account to pay out to, on an automatic daily payout schedule.
- The Stripe CLI, signed in to that sandbox. The probe uses it only to receive webhook deliveries.
  Without it the probe still runs and says that deliveries were not captured.
- Node 26.
- Two environment variables. Keep both out of the repository, issues and chat:
  - `STRIPE_SANDBOX_SECRET_KEY`: the sandbox's secret key (`sk_test_` or `rk_test_`);
  - `STRIPE_SANDBOX_CONNECTED_ACCOUNT`: the connected account id (`acct_`).

## Run it

From the repository root:

```
node scripts/stripe-probe.mjs run
```

The run makes six test charges on the connected account: three by card and three by ACH, including
a decline, a failed debit and a disputed debit. It waits for each ACH payment to finish and for the
dispute to appear, up to ten minutes each, so allow up to forty minutes. `--wait-minutes=20` waits longer;
`--no-listen` skips webhook capture; `--port=4242` chooses the local port the Stripe CLI forwards to.

Each line it prints is one answer. When it ends it names a `findings.md`.

## What it writes

Everything goes under `stripe-probe.local/<run>/`, which git ignores:

- one numbered file for every request and response, and one for every webhook delivery;
- `findings.md`, the answers in words, with every identifier already replaced by a synthetic one.

The numbered files hold the sandbox's real identifiers. The key and the webhook signing secret are
never written.

Paste `findings.md` into issue #513. It is safe to post.

## Payouts, on a later day

Stripe pays out on its own schedule, so the first run usually finds no payout. A day or two later,
add payout evidence to the same run:

```
node scripts/stripe-probe.mjs payouts stripe-probe.local/<run>
```

It lists the payouts and, for each automatic one, the lines Stripe says it covers. It never creates a
payout: which charges a payout covers has to come from Stripe.

## Commit the recorded payloads

```
node scripts/stripe-probe.mjs scrub stripe-probe.local/<run>
```

This copies the numbered files to `tests/fixtures/stripe/<run>/`. Every identifier is replaced with a
synthetic one, the same one each time it recurs, and names, addresses, bank details, device details
and URLs are removed. If anything real is left the command writes nothing and says what it found.

Read the diff before committing it. The scrubber works from patterns and a list of field names, and
a person is the check on what it does not know about. Continuous integration then runs the same
check on every committed payload.

## If something goes wrong

- **The key is refused at the first call.** The message says so. If it names the API version, the
  version the adapter is pinned to is wrong for this account; report that on the issue.
- **A payment method is not accepted on the connected account.** That is a finding, not a failure.
  The run records it and goes on.
- **No dispute arrived.** Run again with a longer `--wait-minutes`.
- **Webhook deliveries were not captured.** Check `stripe listen --print-secret` prints a secret,
  then run again.
- **A live-mode object was returned.** The probe stops. The key or the connected account is not the
  sandbox's; check both before running again.
- **Starting over.** Each run makes new charges and its own directory. Delete a run's directory to
  discard it; nothing else refers to it.
