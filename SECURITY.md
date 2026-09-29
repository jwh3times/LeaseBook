# Security Policy

LeaseBook handles fiduciary trust funds and financial data for multiple organizations, so we take security seriously
and welcome reports from the community. This document explains how to report a vulnerability and what to
expect in return.

## Supported versions

LeaseBook is **pre-release and under active development**. There is not yet a formal supported-version
matrix; security fixes land on the `main` branch and ship through the next automatic tagged release.
Once beta support begins, this section will list the versions that receive security updates.

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues, pull requests, or
discussions.**

Instead, use **[GitHub's private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)**:
open the repository's **Security** tab and choose **"Report a vulnerability."** This creates a private
advisory visible only to you and the maintainers.

If you prefer email, send reports to **<jerryholland00@gmail.com>** with the subject line
`[SECURITY] LeaseBook`.

A good report includes:

- A clear description of the issue and its impact.
- The component or endpoint affected (and the affected version/commit, if known).
- Step-by-step instructions or a proof of concept that reproduces it.
- Any relevant logs, screenshots, or configuration — with secrets and personal data redacted.

Please give us a reasonable opportunity to investigate and fix the issue before any public disclosure,
and avoid accessing, modifying, or deleting data that isn't yours while researching.

## What to expect

As a small project we respond on a best-effort basis. We aim to:

- **Acknowledge** your report within a few business days.
- **Confirm** the issue and assess severity, keeping you updated as we investigate.
- **Fix** valid vulnerabilities as a priority and coordinate disclosure timing with you.
- **Credit** you in the advisory when a fix ships, if you'd like to be named.

## Scope

LeaseBook treats the following as security-critical, and reports in these areas are especially valued:

- **Cross-organization isolation.** Organization isolation is enforced by PostgreSQL row-level security in addition to
  application-layer checks; any path that reads or writes another organization's data is a high-severity
  bug.
- **Ledger integrity.** The financial journal and audit log are append-only — the runtime database role
  has no `UPDATE`/`DELETE` grant on them. Any way to mutate or delete posted financial records is in
  scope.
- **Authentication and authorization.** Auth bypass, privilege escalation across roles, MFA weaknesses,
  session fixation, or missing authorization on an endpoint.
- **Common web vulnerabilities.** Injection, XSS, CSRF (the app uses antiforgery tokens on unsafe
  requests), SSRF, insecure deserialization, and similar.
- **Secret and data exposure.** Leaked credentials, secrets committed to source, or sensitive data
  disclosed in responses or logs.

### Already automated

These run on the repository continuously, so a report that only restates what they already flag —
without a demonstrated impact on LeaseBook — is unlikely to be new to us:

- **Secret scanning:** gitleaks on every pull request and push to `main`, plus GitHub secret scanning
  with push protection.
- **Static analysis:** CodeQL (C#, JavaScript/TypeScript, Python, and GitHub Actions workflows).
- **Dependencies:** Dependabot version and security updates for NuGet, npm, Docker images, and
  GitHub Actions; every action is pinned to a full commit SHA.

### Out of scope

- Findings that require a compromised host, physical access, or an already-privileged
  account/configuration.
- Denial-of-service via volumetric flooding, and automated scanner output without a demonstrated,
  reproducible impact.
- Social engineering, phishing, or physical attacks against contributors or infrastructure.
- Reports against third-party dependencies that should be filed with the upstream project (though we
  appreciate a heads-up).

## Security review history

LeaseBook's security controls have been reviewed twice before beta. Both reviews were **internal**:
they were performed with an AI coding agent (Claude Code) under maintainer direction, not by an
independent third-party firm, and they are not a substitute for one.

- **June 2026: white-box code review and grey-box penetration test.** An audit of the application's
  trust boundaries, followed by authenticated probing of the full stack running in Production mode
  with two organizations. Organization isolation held under live probing: reads of another
  organization's records returned not-found or empty results, writes to them were blocked, and
  nothing distinguished another organization's record from a nonexistent one. No SQL injection,
  unsafe deserialization, command execution, server-side request forgery, or unsafe HTML rendering
  was found.
- **September 2026: follow-up review** of the hardened application, the infrastructure-as-code, the
  CI pipeline, and repository configuration.

Neither review found a high- or critical-severity issue. The classes of issue found were:

- **Authentication and sessions:** MFA enrollment and recovery, and session lifetime and server-side
  revocation.
- **Secrets and key material:** encryption of MFA secrets at rest, durable storage of the
  application's data-protection keys, and recoverability of vault-held keys.
- **Transport and browser security:** database TLS certificate validation, cookie `Secure` behavior
  behind a TLS-terminating proxy, security headers, and host filtering.
- **Output handling:** spreadsheet formula injection in CSV exports.
- **Information disclosure:** detail in health endpoints and server banners, and personal data in
  request URLs.
- **Audit integrity:** actor attribution on audit records.
- **Input bounds:** request-size and value limits on uploads and telemetry.
- **Build and supply chain:** CI action pinning and allow-listing, container image pinning and
  non-root images, and secret-scanning coverage.
- **Development and infrastructure configuration:** seeded fixture credentials, local service
  exposure, and drift between database role-bootstrap scripts.
- **Documentation accuracy:** places where security documentation had fallen behind the
  implementation.

Findings were tracked privately and fixed on `main`. A small number of low-severity, design-level
items were assessed and accepted as residual risk; they remain tracked privately. LeaseBook is not
yet publicly deployed, so controls that only a live deployment can prove, such as ingress and
network configuration, are not claimed here and will be verified at deployment.

## Safe harbor

We consider good-faith security research conducted under this policy to be authorized. We will not pursue
or support legal action against researchers who:

- Make a good-faith effort to avoid privacy violations, data destruction, and service disruption.
- Only interact with accounts and data they own or have explicit permission to test.
- Report promptly and give us a reasonable chance to remediate before public disclosure.

Thank you for helping keep LeaseBook and its users safe.
