# Mutation testing

- **Audience:** Contributors, reviewers and maintainers
- **Status:** Living method and measurement record
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-14

Mutation testing asks whether the tests would notice the code being wrong. A tool changes the code
one small edit at a time, such as `>` to `>=`, `+` to `-`, or a call removed, and reruns the tests
against each edit. Each edited copy is a mutant. A mutant some test fails on is killed. A mutant
every test still passes on survived: either the tests have a gap there, or the edit changes nothing
anyone could observe.

It runs nightly on the two modules where a wrong answer is a fiduciary error, Accounting and Payments.
This page records how it runs, how to run it locally, how to read its summary, and what to do about
a survivor. The [ADR-055 addendum](../adr/ADR-055-test-coverage-measurement.md#addendum--mutation-testing-with-strykernet-2026-10-14)
records the tool and why. [Test coverage](coverage.md) is the other half: it finds code no test runs,
and this finds code the tests run without checking.

## What runs

| Module     | Test project it is mutated against | Stryker configuration                                  |
| ---------- | ---------------------------------- | ------------------------------------------------------ |
| Accounting | `tests/LeaseBook.Tests.Accounting` | `tests/LeaseBook.Tests.Accounting/stryker-config.json` |
| Payments   | `tests/LeaseBook.Tests.Payments`   | `tests/LeaseBook.Tests.Payments/stryker-config.json`   |

The tool is Stryker.NET with its Microsoft Testing Platform runner, pinned as the `dotnet-stryker`
local tool. That runner reruns the whole test project for every mutant a test covers, so each module
has a fast test project of its own and is never mutated against the integration suite, which boots
the host. Both test projects run against a Postgres container, so Docker must be running.

`LeaseBook.Tests.Payments` exists for this. Its tests drive `PaymentEngine` and `SettlementEngine`
directly, as the app role on the real schema, with no host. The three ports the engines call are
stand-ins the test controls: the ledger records what Payments asked to post, and eligibility and the
fee rules answer what the test says. What the ledger does with a request belongs to Accounting and is
tested in `LeaseBook.Tests.Accounting`. The Payments tests pin the decision Payments made: what it
asked to post, what it sent to a person and with which reason, and how each payment and payout moved
from one state to the next. The payment tests in `LeaseBook.Tests.Integration` stay where they are.

Each configuration mutates every source file of its module except these:

| Excluded                                                            | Why                                                                                                                                                                                                                                                                                                                            |
| ------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Persistence/*Configuration.cs`, `*Configurations.cs`               | The EF model's mapping. Most of it is schema detail, such as column lengths, precision and constraint names, which only the migrations generator reads; the EF drift check in CI guards it. The parts that matter at runtime, table and key mapping, fail every test at once when broken, so they add run time and say little. |
| `Persistence/AccountingSql.cs`                                      | Used only by those configurations, to write constraint text.                                                                                                                                                                                                                                                                   |
| `Endpoints/` (Accounting)                                           | Bind, dispatch and map, with no logic by convention. Their tests are HTTP tests in the integration suite, which Stryker does not run, so every mutant there would show as not covered and bury the survivors that matter.                                                                                                      |
| `AccountingModuleServiceCollectionExtensions.cs`, `ModuleMarker.cs` | Service registration and an empty marker type. The host booting is what tests registration.                                                                                                                                                                                                                                    |
| Anything under `obj/`                                               | Generated code.                                                                                                                                                                                                                                                                                                                |

Stryker still lists an excluded file in its report, with no mutants. The summary counts a file as
mutated only when it has one.

The nightly run is [`.github/workflows/mutation.yml`](../../.github/workflows/mutation.yml), at 00:17
UTC and on manual dispatch, where a module can be chosen. It never runs on a pull request. It runs
three jobs in parallel: Payments, and Accounting in two parts. A whole Accounting run comes too near
GitHub's six-hour limit for a hosted job (see [Cost](#cost)), and a job that times out reports
nothing. Part 1 mutates the directories `scripts/mutation.mjs` names in `firstPart`, and part 2 the
rest of what the configuration mutates, so a directory added later falls into part 2 and is never
left out. Each job writes the summary to its job summary and uploads `mutation-<run>` (`payments`,
`accounting-1` or `accounting-2`), which holds the summary, the HTML report
(`reports/mutation-report.html`) and the JSON report the summary was read from.

## Run one module locally

Docker must be running. From the repository root:

```bash
dotnet tool restore
node scripts/mutation.mjs payments --concurrency 4
```

`accounting` and `all` work the same way. The runner runs Stryker from the module's test project,
writes everything under `StrykerOutput/<module>/` (open `reports/mutation-report.html`), and prints
the summary. `--out-md <file>` also writes the summary to a file.

A whole module takes a long time, Accounting most of all; see [Cost](#cost). To mutate part of a
module, name the files with `--mutate`:

```bash
node scripts/mutation.mjs accounting --concurrency 4 --mutate '**/LeaseBook.Modules.Accounting/Posting/*.cs'
node scripts/mutation.mjs payments --concurrency 4 --mutate '**/Processing/SettlementEngine.cs'
```

A `--mutate` given on the command line replaces the configuration's list instead of adding to it, so
the exclusions above no longer apply. Name only the files you mean.

To run one of Accounting's two parts, as the nightly job does, which writes to
`StrykerOutput/accounting-<part>/`:

```bash
node scripts/mutation.mjs accounting --part 1 --concurrency 4
```

`--part` builds the list itself from the configuration's, so the exclusions still apply, and it
takes no `--mutate`.

To render the summary again from a report a run already wrote, without running anything:

```bash
node scripts/mutation.mjs payments --report StrykerOutput/payments/reports/mutation-report.json
```

Keep `--concurrency` at 4 or below on a shared machine. Each concurrent session runs its own test
process and its own Postgres container.

If Stryker stops with `CS1705` naming two versions of an EF Core assembly, the local restore is
stale. Run `dotnet restore LeaseBook.slnx` and try again. CI always restores fresh.

## Read the summary

For each module, the summary gives:

- **The mutation score.** The mutants a test noticed, killed or timed out, as a share of every mutant
  that compiled and was not ignored. It is never compared with a threshold.
- **Counts by status.**

  | Status        | Meaning                                                                                                                                             |
  | ------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- |
  | Killed        | A test failed against the mutant.                                                                                                                   |
  | Survived      | The tests ran the mutated code and none failed.                                                                                                     |
  | Timeout       | The tests ran far longer than without the mutant, usually because it made a loop endless. Counted as noticed.                                       |
  | No coverage   | No test ran the mutated code. A survivor nobody even looked at.                                                                                     |
  | Compile error | The mutant does not compile. Stryker sometimes marks a whole method so when one of its mutants fails to compile; see [Known limits](#known-limits). |
  | Ignored       | Stryker left the mutant out, for example a block removal already covered by the statement removals inside it.                                       |

- **A table of every undetected mutant**, survived or not covered: the file, the line, the kind of
  edit (Stryker's mutator name), the original text and its replacement, each shortened to 60
  characters, and whether the equivalents list explains it.
- **Stale entries**: entries in the equivalents list that match no undetected mutant in a file this
  run mutated. The line changed, or a test now kills the mutant. Remove or update each one.

Stryker marks its Microsoft Testing Platform runner as a preview. Confirm a survivor by hand before
acting on it: make the edit yourself, run the test project, and see every test pass.

## A survivor: kill it or explain it

Most survivors are gaps. Write the test in the module's fast test project, so the next nightly run
kills the mutant. A test that only the integration suite would fail does not count: Stryker never
runs that suite.

A survivor is equivalent when the edit changes nothing any caller can observe through any input the
code can receive. Some examples:

- `<` becomes `<=` where the two sides can never be equal, because an earlier check returned first.
- A statement is removed whose effect the next statement does again.
- The text of an exception message changes, where nothing reads the message.

These are not equivalent, and need a test instead:

- The edit only matters for an input that is hard to construct in a test.
- The edit changes a value that is stored or logged but not yet asserted on.
- Another test project would fail.

To record an equivalent mutant, add an entry under the module in
[`tests/mutation/equivalent-mutants.json`](../../tests/mutation/equivalent-mutants.json). This one
shows the shape only, and claims nothing about that mutant:

```json
{
  "file": "src/LeaseBook.Modules.Payments/Processing/PaymentEngine.cs",
  "mutator": "Equality mutation",
  "original": "if (op.LeaseUntil > Now || op.DueAt <= Now || op.Reason is not null) { return false; }",
  "replacement": "op.LeaseUntil >= Now",
  "reason": "Why no input can tell the two apart, in a sentence or two."
}
```

- `file` is the path from the repository root.
- `mutator` is the mutator name exactly as the summary shows it.
- `original` is the whole source line the mutant starts on, as it reads in the file. Whitespace runs
  count as one space and the ends are trimmed, so indentation does not matter.
- `replacement` is the replacement text as the HTML report shows it, in full. The summary table
  shortens long replacements, so copy it from the report.
- `reason` is required.

An entry matches on content, never on Stryker's mutant id or line number, which both shift whenever
the file changes. So an entry keeps matching when lines above it move, and stops matching, and is
reported stale, when its own line changes. A changed line is a reason to look again.

`node --test scripts/mutation.test.mjs` checks that the file is well formed: every module present,
each gate a boolean, every entry complete with a reason.

## The gate

Each module has a gate in the same file, `"gate": false` or `"gate": true`.

- **Off**, as both ship: the run reports and passes, whatever survived.
- **On**: the run fails when the module has an undetected mutant, survived or not covered, that the
  equivalents list does not explain.

Turn a module's gate on in the change that explains or kills its last unexplained survivor. From then
on, a change that leaves an edit unnoticed shows up as a red nightly run.

The run also fails, gate or no gate, when Stryker fails or writes no report. The summary then says
the module was not measured.

## Known limits

- **One coverage pass, then every covered mutant reruns the whole test project.** The Microsoft
  Testing Platform runner cannot run a subset of a test project or map tests to mutants. Per-test
  coverage capture would cost one test session per test and gain nothing, so both configurations
  set `coverage-analysis` to `all`: the initial test run finds which mutants no test reaches, and
  those are not run.
- **Safe mode.** When a mutant makes the file fail to compile in a way Stryker cannot pin to that
  mutant, it removes every mutation in the method and marks them as compile errors. In Payments this
  happens to `PaymentEngine.PostReturnAsync`, whose condition assigns a pattern variable in one
  operand of `||`. In Accounting it happens to the validator in `GetOwnerEquityLines.cs` and to the
  handler in `PostRefundCheck.cs`. Their mutants are counted as compile errors, not tested.
- **Per-session cost.** Each test session starts its own Postgres container and applies the
  migrations, which is a large part of each mutant's time.

## Cost

Measured locally on 2026-10-14, at concurrency 4 on a 16-core machine. GitHub's hosted runners are
slower; read these as proportions.

| Run                         | Tests | Mutants created | Mutants tested | Time  | Wall time per tested mutant |
| --------------------------- | ----: | --------------: | -------------: | ----- | --------------------------: |
| Payments, the whole module  |   164 |           1,881 |            928 | 54:45 |                       3.4 s |
| Accounting, `Posting/` only |   211 |           1,777 |            109 | 14:55 |                       7.2 s |

Stryker creates every mutant of the module before it applies the `mutate` list, so the Accounting
row creates as many as a whole run would. In both runs the build, the initial test run and the
coverage pass took under two minutes; the rest is testing mutants. An Accounting session runs
slower than a Payments one: it has more tests, and its property and golden-file tests are heavier.

A whole Accounting run, with the exclusions above, should test somewhere near 1,000 mutants. Of its
1,777, the coverage pass found 1,344 reached by a test, and some of those do not compile (156 in the
module do not), are ignored, or are in excluded files. At 7.2 s each that is about 2 hours locally.
The estimate before this measurement was 4 hours, for every compiling mutant at 35 s a session.

Per-test coverage capture, Stryker's default, runs one test session per test, each starting its
own Postgres container: 164 sessions for Payments before any mutant is tested. The aggregate pass
the configurations use took 24 seconds.

On GitHub's hosted runner (four cores), measured on 2026-10-14 from this branch's first run of the
workflow, whole modules at concurrency 4:

| Run                          | Mutants tested | Time    | Wall time per tested mutant |
| ---------------------------- | -------------: | ------- | --------------------------: |
| Payments, the whole module   |            928 | 1:28:28 |                       5.7 s |
| Accounting, the whole module |            956 | 4:55:40 |                      18.6 s |

The whole Accounting run took 82% of GitHub's six-hour limit for a hosted job, and a job that times
out reports nothing, so the workflow runs Accounting in two parts. Part 1 held 468 of those 956
mutants and part 2 the other 488, so each should take about two and a half hours. Each part has a
240-minute timeout and Payments 180. Re-measure after the first scheduled runs, and move a
directory between the parts if one grows much longer than the other.

## Baseline

Measured on 2026-10-14, with an empty equivalents list.

**Payments, the whole module: 54.0%.** 750 killed, 178 survived, none timed out, 460 not covered,
121 compile errors, 169 ignored. The 638 undetected mutants are the first survivor list.

| File                             | Score | Killed | Survived | No coverage | Compile error |
| -------------------------------- | ----: | -----: | -------: | ----------: | ------------: |
| `Processing/PaymentEngine.cs`    | 81.0% |    362 |       83 |           2 |            52 |
| `Processing/SettlementEngine.cs` | 84.7% |    355 |       62 |           2 |            21 |
| Everything else in the module    |  6.3% |     33 |       33 |         456 |            48 |

The two engines are what `LeaseBook.Tests.Payments` tests so far. 456 of the 460 mutants no test
reaches are elsewhere: the refund checks and the MICR line, and the query and command handlers
under `Features/`, which the integration suite tests. 34 of `PaymentEngine`'s 52 compile errors are
`PostReturnAsync`, removed by safe mode; the other 18 are edits that do not compile. Most of the engines' survivors fall into a few
kinds: an advisory lock removed, which a test running one thing at a time cannot see; a repeated
binding or fixture check removed where an earlier check already refused; a `<=` against `<` at a
boundary the tests do not reach; and the text of a lock name or a status.

**Accounting, `Posting/` only: 60.7%.** 82 killed, 27 survived, none timed out, 26 not covered, 9
compile errors, 49 ignored, across five files.

**Accounting, the whole module: 49.0%**, from the hosted run. 572 killed, 384 survived, none timed
out, 211 not covered, 155 compile errors, 220 ignored, across 71 files. The 595 undetected mutants
are the module's first survivor list. In the same run Payments scored 54.0% again: 748 killed, 179
survived and 1 timed out. Of the 750 the local run killed, one survived here and one timed out.
