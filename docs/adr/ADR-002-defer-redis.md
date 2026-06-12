# ADR-002: Defer Redis

- **Status:** Accepted
- **Date:** 2026-06-12
- **Deciders:** Engineering

## Context

The architecture blueprint originally listed Redis (Azure Cache) for sessions and rate limiting.
Adding Redis now is a managed service to provision, secure, and pay for. We should only adopt it
against a concrete need.

## Decision

**Defer Redis.** Authentication uses same-origin cookie sessions (ASP.NET Core Identity, WP-06);
background jobs are Postgres-backed (ADR-001). Rate limiting, when needed for auth endpoints (M8),
will start with ASP.NET Core's built-in in-process rate limiter. Nothing in Phase 1 requires a
distributed cache.

## Consequences

- One fewer service to operate, secure, and fund — appropriate for a solo operator and a
  scale-to-zero/single-replica dev posture.
- If we later run multiple app replicas needing shared rate-limit state or a distributed cache,
  we will add Redis then.

## Revisit trigger

We scale the app to multiple concurrent replicas needing shared state (distributed rate limiting,
shared cache, or backplane), or a performance need for a cache surfaces — revisit at Phase 2.
