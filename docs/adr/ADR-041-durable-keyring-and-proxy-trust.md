# ADR-041: The keyring is durable, and proxy trust is declared

- **Status:** Accepted
- **Date:** 2026-08-18
- **Deciders:** Jerry Holland

## Context

Two hardening items were deferred out of the M8 security pass and then survived the prod-networking
work untouched. Both were recorded as "Track B owns it", on the reasoning that neither could be
validated without a live environment. That reasoning held for validation and quietly became an excuse
for the configuration itself: after two work packages the application still shipped with a bare
`AddDataProtection()` and no forwarded-headers configuration at all.

**The keyring.** The M8 security pass encrypts the Identity token store — TOTP secrets and recovery
codes — at rest, through an EF value converter over ASP.NET Core Data Protection. The keys that do the encrypting were left on the default provider, which persists to
the local filesystem. In a container that filesystem does not survive a recreate. Losing the keyring
does not leak anything; it makes every encrypted row permanently unreadable, which locks every
enrolled account out of its second factor. The control introduced to protect those secrets had made
their availability depend on a directory inside a disposable container.

**Proxy trust.** The auth rate limiter partitions on the connection's remote address. Behind any
reverse proxy that address belongs to the proxy, so every client lands in one partition. Nothing in
the application declared which proxy to believe, and the framework's default — trust loopback —
silently produces exactly that collapsed partition rather than an error.

The shared property is that both failed _quietly_. A filesystem keyring works perfectly until the
first recreate. A collapsed rate-limit partition looks identical to a working one until someone
measures it.

## Decision

**1. The keyring persists to Postgres in every environment, and is wrapped by Key Vault where
deployment config names a key.**

Not blob storage, which was the original remediation note. The database is already durable,
private-networked, backed up and restorable, and — decisively — it is present in development, in
Docker Compose and in CI. That makes the property testable: a test protects a payload, discards the
provider entirely, builds a new one against the same database, and reads the payload back. The
failure this ADR exists to prevent is now a red test rather than an intention.

Persistence alone would be a weaker posture than the original note, because the key material would
sit in the same database as the ciphertext it protects — so a database compromise would yield both.
`DataProtection:KeyVaultKeyUri` therefore wraps the persisted keys with a Key Vault key, granted to
the app's existing managed identity as **Crypto User** (wrap/unwrap only, never key creation or
deletion). Unset, the keyring is still durable and the application says so at boot.

**2. The keyring gets its own `DbContext`, and this is not a stylistic preference.**
`AppDbContext` takes an `IDataProtectionProvider` to build the token-store converter, so persisting
the keyring into it would make the provider depend on the context that depends on the provider. It
would also be refused on arrival: ADR-039 makes `AppDbContext` reject a write that declares no actor,
and key rotation has none — it is not a unit of work anyone is accountable for. `KeyringDbContext`
maps one table and depends on nothing else. The table is still declared on `AppDbContext`'s model, so
the single migration history owns it and the schema guard can see it.

**3. `data_protection_keys` is global-class, and not append-only.** It has no `org_id` and no RLS,
because the keyring belongs to the deployment rather than to an organization; it is allowlisted
alongside `orgs` and `feature_flags`. It keeps its default `UPDATE`/`DELETE` grants because Data
Protection revokes a key by rewriting it — this is operational state, not a fiduciary record.

**4. Forwarded headers are off unless deployment config names the proxy, and an enabled-but-empty
configuration is refused at startup.** Believing `X-Forwarded-For` is a trust decision: whoever can
reach the application directly can claim any client address once the header is honoured. So the
default is to believe nobody, and turning it on requires naming a proxy or a network. Naming nothing
throws rather than falling back to the framework's loopback default, because that fallback ignores
every header a real ingress sends while reading, in configuration, as though the problem were solved.

**5. A production that is not yet configured says so.** Both settings are supplied by deployment config rather than
a committed file, matching `AllowedHosts`. Neither can be validated before an environment exists — so
in Production the application logs a warning at boot for each one that is still unset. The gap stays
open until Track B, but it stops being silent.

## Consequences

The keyring survives a container recreate, and a test proves it rather than a deployment discovering
it. A third test protects a payload through the **running host** and reads it back through a provider
that shares only the database, so a future change that quietly drops the persistence — or alters the
application-name discriminator — fails instead of degrading.

Writing that test surfaced a real wiring gap: the integration `ApiFactory` re-pointed only
`AppDbContext` at the test container, so the booted host's keyring reached for the developer's local
database. Every other read went to the container. It is the same hazard the factory's own comment
already documented for Hangfire, and it now covers both contexts.

A second `DbContext` means EF tooling can no longer infer which one owns migrations. Every
`dotnet ef` invocation in CI and the deploy workflows now passes `--context AppDbContext`, as the
migrator image's bundle already did. This is more explicit than what it replaced, but it is a real
ripple: a future context added without updating those call sites breaks the build rather than the
runtime.

The explicit OpenAPI-build lifecycle in
[ADR-042](ADR-042-explicit-host-process-lifecycle.md) excludes the whole durable keyring, not just the
Key Vault wrap. The schema-drift build runs the application to completion with no
`ASPNETCORE_ENVIRONMENT`, so Production settings apply with no database and no Azure identity — and
startup does read the key ring. Persisting there fails with a cryptographic exception instead of
emitting a document. That build produces a schema and needs no durable keys, so its lifecycle keeps
the in-memory default.

Forwarded-header trust ships **off**, so the rate-limit partition is unchanged until an operator names
the ingress. What changed is that the configuration now exists, refuses to be half-set, and announces
its own absence in Production. The remaining step is genuinely operator work: name the ingress
network, then confirm partitioning from two distinct source addresses against the deployed host.

## Revisit trigger

Revisit the keyring decision if key material ever needs to be readable by something that is not this
application — a second service, or an out-of-band decryption tool — since the argument for co-locating
it with the data rests on there being exactly one reader. Revisit the proxy decision if a second hop
is introduced in front of the ingress, which makes `ForwardLimit` a real choice rather than the
one-hop default it is today.
