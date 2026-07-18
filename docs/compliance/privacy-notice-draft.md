# Privacy Notice — Draft Skeleton

- **Audience:** External compliance/legal reviewer and maintainers
- **Status:** Draft — legal review required before any customer-facing use
- **Owner:** Maintainers
- **Last reviewed:** 2026-07-18

> **This is a skeleton, not a privacy notice.** It is engineering-authored scaffolding for a
> GLBA-style (Regulation P) consumer privacy notice. It is **not legal advice and must not be shown to
> any customer in this form.** Every sentence that asserts a legal conclusion — what the law requires,
> what LeaseBook's role is, what is shared, and what rights apply — carries a `[LEGAL REVIEW]` marker
> and must be confirmed or replaced by a licensed attorney during the compliance review. The factual
> data-collection categories are drawn from the [data-handling description](data-handling.md); the
> legal framing around them is not.
>
> **Threshold question — `[LEGAL REVIEW]`.** Whether this notice is issued by the managing brokerage
> (with LeaseBook as its service provider) or by LeaseBook itself, and whether a GLBA/Regulation P
> notice is required at all for this relationship, is the first determination the review must make.
> The section structure below follows the federal model privacy form and is a placeholder for that
> decision. `PROVIDER` below is the party the review determines must issue the notice.

## FACTS — what does PROVIDER do with your personal information?

**Why?** `[LEGAL REVIEW]` Financial companies choose how they share your personal information. Federal
law gives consumers the right to limit some but not all sharing. Federal law also requires PROVIDER to
tell you how it collects, shares, and protects your personal information.

**What?** The types of personal information collected and held depend on your relationship with
PROVIDER. Drawn from the [data-handling description](data-handling.md), this information can include:

- Name and contact information (email address, phone number)
- Property address
- Account balances and transaction history
- Bank account identifiers (stored masked — last four digits only)

Whether each category is "nonpublic personal information" under the applicable rule is `[LEGAL
REVIEW]`.

**How?** `[LEGAL REVIEW]` All financial companies need to share customers' personal information to run
their everyday business. In the section below, PROVIDER lists the reasons it can share personal
information, whether PROVIDER shares, and whether you can limit that sharing.

## Reasons we can share your personal information — `[LEGAL REVIEW]`

| Reason for sharing                                                                        | Does PROVIDER share? | Can you limit this sharing? |
| ----------------------------------------------------------------------------------------- | -------------------- | --------------------------- |
| Everyday business purposes (process transactions, maintain accounts, report to the owner) | `[LEGAL REVIEW]`     | `[LEGAL REVIEW]`            |
| Marketing purposes                                                                        | `[LEGAL REVIEW]`     | `[LEGAL REVIEW]`            |
| Joint marketing with other financial companies                                            | `[LEGAL REVIEW]`     | `[LEGAL REVIEW]`            |
| Affiliates or nonaffiliates for their own purposes                                        | `[LEGAL REVIEW]`     | `[LEGAL REVIEW]`            |

_Engineering note (not customer-facing): the product does not sell customer data and shares personal
information only as needed to provide the service to the managing brokerage; the legal
characterization of that statement is `[LEGAL REVIEW]`._

## How does PROVIDER protect my personal information? — `[LEGAL REVIEW]`

`[LEGAL REVIEW]` To protect your personal information from unauthorized access and use, PROVIDER uses
security measures that comply with applicable law. These include the technical and organizational
safeguards described in the [data-handling and safeguards document](data-handling.md): encryption in
transit and at rest, per-organization data isolation, least-privilege access, an append-only audit
trail, and secret management via a managed key vault.

## How does PROVIDER collect my personal information? — `[LEGAL REVIEW]`

`[LEGAL REVIEW]` PROVIDER collects your personal information when the managing brokerage opens or
maintains your account, when statements and disbursements are processed, and when account data is
imported during onboarding.

## Who is providing this notice? — `[LEGAL REVIEW]`

`[LEGAL REVIEW]` Provider identity, contact method, and effective date to be supplied once the
threshold question above is resolved.

## Definitions — `[LEGAL REVIEW]`

- **Nonpublic personal information** — `[LEGAL REVIEW]`
- **Affiliates** — `[LEGAL REVIEW]`
- **Nonaffiliates** — `[LEGAL REVIEW]`
- **Joint marketing** — `[LEGAL REVIEW]`

## Open questions for the review

1. `[LEGAL REVIEW]` Is LeaseBook a "financial institution" under GLBA, or a service provider to one,
   and who must issue this notice?
2. `[LEGAL REVIEW]` Is a Regulation P notice required for this owner / tenant / brokerage relationship?
3. `[LEGAL REVIEW]` Which collected categories are nonpublic personal information, and do any North
   Carolina state-law notices apply in addition?
4. `[LEGAL REVIEW]` Are there opt-out rights to describe, or does all sharing fall under everyday
   business exceptions?
5. `[LEGAL REVIEW]` Required retention and secure-disposal language, aligned with the trust-record
   retention minimum in [data-handling.md](data-handling.md) §5.
