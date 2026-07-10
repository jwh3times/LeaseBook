# LeaseBook Documentation

- **Audience:** Evaluators, contributors, operators, and maintainers
- **Status:** Living index
- **Owner:** Maintainers
- **Last reviewed:** 2026-07-09

Use this page to find the maintained source for a question. Documents intentionally have one owner;
summaries elsewhere should link here rather than restating mutable detail.

## Start Here

| Need                                              | Document                                               |
| ------------------------------------------------- | ------------------------------------------------------ |
| Understand the product and run it locally         | [Project README](../README.md)                         |
| Understand supported scope and explicit non-goals | [Product scope](product-scope.md)                      |
| Understand the implemented system                 | [Architecture](architecture.md)                        |
| Understand trust-accounting behavior              | [Accounting](accounting.md)                            |
| Set up or operate a development environment       | [Local-development runbook](runbooks/local-dev.md)     |
| Contribute a change                               | [Contributing guide](../CONTRIBUTING.md)               |
| See shipped capabilities and broad direction      | [Roadmap](ROADMAP.md) and [changelog](../CHANGELOG.md) |

## Reference

- [Architecture blueprint](blueprint.md) records the pre-M0 technical baseline. Accepted ADRs and
  the implemented architecture supersede it where the system evolved.
- [Architecture Decision Records](adr/README.md) preserve significant engineering decisions and
  their revisit triggers.
- [Parallel-run checklist](migration/parallel-run.md) supports a migration overlap period.
- [Infrastructure guide](../infra/README.md) and
  [Azure database bootstrap](../infra/db/azure-bootstrap.md) cover authored Azure infrastructure.

## Runbooks

- [Local development](runbooks/local-dev.md)
- [Point-in-time restore](runbooks/restore.md)

## Governance

- [Documentation policy](documentation-policy.md) defines classification, canonical ownership,
  lifecycle metadata, and the public/private boundary.
- [Security policy](../SECURITY.md), [support](../SUPPORT.md),
  [code of conduct](../CODE_OF_CONDUCT.md), and [contributing](../CONTRIBUTING.md) govern community
  participation.

Internal plans, customer-specific material, security workpapers, and unvalidated research live under
the gitignored `private/` tree and are not required to build, run, test, or understand the public
engineering contract.
