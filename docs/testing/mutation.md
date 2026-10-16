# Mutation testing

- **Audience:** Contributors, reviewers and maintainers
- **Status:** Living method and measurement record
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-16

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
directly, and the module's commands and queries through the real dispatcher and validators, as the
app role on the real schema, with no host. The three ports the payment code calls are stand-ins the
test controls: the ledger records what Payments asked to post, and eligibility and the fee rules
answer what the test says. What the ledger does with a request belongs to Accounting and is tested in
`LeaseBook.Tests.Accounting`. The Payments tests pin the decision Payments made: what it asked to
post, what it sent to a person and with which reason, how each payment and payout moved from one
state to the next, and what the lists show. The refund-check tests are the exception: they run the
host's `RefundCheckLedgerAdapter` over the real Accounting module, so the posting guards a refund
relies on run too. The pure tests of Payments code, such as the MICR line and the convenience fee,
moved here from `LeaseBook.Tests.Integration`; its HTTP and host tests stay there.

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
four jobs in parallel, each module in two parts. A whole run of either module comes too near a
hosted job's time limit (see [Cost](#cost)), and a job that times out reports nothing. Part 1
mutates the directories `scripts/mutation.mjs` names in `firstPart`, `Processing` for Payments and
five directories for Accounting, and part 2 the rest of what the configuration mutates, so a
directory added later falls into part 2 and is never left out. Each job writes the summary to its
job summary and uploads `mutation-<run>` (`accounting-1`, `accounting-2`, `payments-1` or
`payments-2`), which holds the summary, the HTML report (`reports/mutation-report.html`) and the
JSON report the summary was read from.

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

To run one of a module's two parts, as the nightly jobs do, which writes to
`StrykerOutput/<module>-<part>/`:

```bash
node scripts/mutation.mjs accounting --part 1 --concurrency 4
node scripts/mutation.mjs payments --part 2 --concurrency 4
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

### A lock or a race

A mutant that removes an advisory lock survives every test that runs one thing at a time.
`LeaseBook.Tests.Payments` kills these with races. `PaymentsHarness.RaceAsync` works in four steps:

1. It runs the first call on a connection of its own and keeps that transaction open.
2. It starts the second call on another connection.
3. It waits until the second has finished, or Postgres shows it waiting on a lock it has not been
   granted.
4. Only then does it commit the first.

With the lock in place, the second call waits and reads what the first committed. Without it, the
second runs on the state from before the first, and the test's assertion on the outcome fails.
Nothing sleeps while waiting for the other call, so every run gives the same result.

Changing a lock name's text is equivalent only when one place builds that name, so that every caller
gets the same change. Several Payments lock names are written out in more than one place. Emptying
one copy then gives two callers different locks for the same thing, and a race test kills it.

### A guard against a state no code makes

Some guards refuse a state no code path can produce. One example is a payment fixture whose bank or
account changed after payments were made under it: the app role cannot update `payment_fixtures`,
and an organization has one fixture. These guards still get a test, which should go red when the
guard is removed. The test reaches the state the only way it can arise, by editing the row as the
migrator (`PaymentsHarness.RebindAsync`).

## The gate

Each module has a gate in the same file, `"gate": false` or `"gate": true`.

- **Off**: the run reports and passes, whatever survived. Accounting's gate is off.
- **On**: the run fails when the module has an undetected mutant, survived or not covered, that the
  equivalents list does not explain. Payments' gate is on, since every Payments mutant was killed or
  explained on 2026-10-16.

Turn a module's gate on in the change that explains or kills its last unexplained survivor. From then
on, a change that leaves an edit unnoticed shows up as a red nightly run: write the test, or record
the mutant as equivalent with its reason, in the same change that made it. A split module's gate
applies to each part on its own, so either part's job can go red.

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
- **Per-process cost.** Each test process starts its own Postgres container and applies the
  migrations.
- **Static initializers are not mutated.** See below.
- **A busy runner can time a mutant out.** Both configurations set `timeout-ratio` to 4. See
  [Timeouts](#timeouts).
- **A few mutants are reached only by chance**, so their status can change between runs of the
  same code. See [Chance results](#chance-results).

### Static initializers

The runner reuses a test process for more than one mutant. A static field's initializer runs once
in a process, on first use, so a mutant inside one does not behave like any other mutant:

- If the field was first used before the mutant was switched on, the mutant never takes effect and
  survives, whatever the tests check. Emptying the payment-method map in `LedgerPostingShared.cs`
  survived a whole Accounting run, though 23 tests fail when the same edit is made by hand.
- If the field is first used while the mutant is on, the mutated value stays for the rest of that
  process, and every later mutant tested there runs against it. Those are killed or time out
  without being at fault.

Stryker has no setting that restarts the process for each mutant. On 2026-10-15 the two halves of
Accounting were run where the first whole run had been, on the same code. Leaving timeouts aside
(see [Timeouts](#timeouts)), 51 mutants that had survived were killed, 20 of them static ones, and
one that had been killed survived. Made by hand, a sample of each behaved as the whole run said,
apart from the static mutant above. With the static mutants ignored, the next two runs each changed
only two statuses from the whole run, and every change was a mutant that tests reach only by chance
(see [Chance results](#chance-results)).

So every static field whose initializer Stryker mutated carries a
`// Stryker disable once all : <reason>` comment, and its mutants are reported as ignored with that
reason: 51 in Accounting (the lookup maps in `LedgerPostingShared.cs`, `RefundCheckShared.cs` and
`StatementSectionMap.cs`, the adjustment kinds in `RecordBankAdjustment.cs` and the `DbValues`
arrays in `AccountingEnumConverters.cs`) and 17 in Payments (`RetrySeconds`, `EventKinds` and
`Waiting` in `PaymentEngine.cs`). The tests of those values are not measured by mutation. When a
summary lists a mutant inside a new static initializer, add the comment there too; a test-only
change cannot make that mutant's result trustworthy.

### Timeouts

Stryker stops a mutant's test run once it takes longer than `timeout-ratio` times the initial test
run, plus `additional-timeout` (10 seconds by default), and counts the mutant as timed out, which
the score counts as detected. The initial run is one test process alone. Mutants then run four at a
time, each process with its own Postgres container, and on a four-core hosted runner that makes a
run much slower than the one it is measured against. Locally the initial Accounting run took 38
seconds, and at Stryker's default ratio of 2 each mutant had 85 seconds.

At the default ratio, the two Accounting halves on 2026-10-15 timed out 146 mutants in one run and
120 in the next, against none in the whole run, on the same code, and in different files each time.
They came early in each half, three timeouts to one result, as if three of the four processes were
stuck while one worked. A mutant in part 2 of the second run took 102 seconds per process on
average. Made by hand, one of them, a validator rule removed in `RecordBankAdjustment.cs`, survived
as the whole run said. So both configurations set `timeout-ratio` to 4, which leaves room for a
run four times as slow as the initial one. A mutant that really loops forever now costs about four
test runs before it is stopped. Payments has four that time out, all in `ConvenienceFee.cs`: three
remove the refusal of a fee rule that cannot be grossed up, so its loop never ends, and the fourth
fails three tests in under a second when made by hand, so it timed out sharing a process with one of
the others. A timeout counts as detected either way.

The first run at ratio 4, also on 2026-10-15, timed out no Accounting mutant. Each of the 120 the
run before had timed out took the status the whole run gave it, 95 survived and 25 killed, and
Payments matched the run before mutant for mutant.

### Chance results

A few mutants are reached only by chance, so the same code can give them different statuses from
one run to the next. A status change on one of these does not mean the code or the tests changed.

- **`AccountingPeriods.cs`, the lost-race path of opening a period.** It runs only when two
  requests race to create the same month, which a concurrency test does not always arrange.
- **`GetOwnerLedger.cs`, `rows.Count > 0` made `>= 0`.** Only an owner ledger with no rows tells
  them apart, and only the random sequences of
  `InvariantPropertyTests.Random_valid_sequences_keep_the_invariants_and_converge_after_settlement`
  produce one. Made by hand, the mutant failed one test run in three.

The first scheduled run, on 2026-10-16, gave the same statuses as the run before it except that
both of these survived where both had been killed. Both are survivors to fix with a test that
reaches the case every time. Add a mutant here when a
summary shows a status change that the code does not explain.

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
mutants and part 2 the other 488, so each should take about two and a half hours. Each timed-out
mutant waits out Stryker's timeout and then starts a new test process, so the split runs at the
default timeout ratio took longer (see [Timeouts](#timeouts)):

| Run, 2026-10-15                               | Part 1  | Part 2  | Payments |
| --------------------------------------------- | ------- | ------- | -------- |
| First split run (146 timeouts, all in part 1) | 3:58:31 | 2:00:56 | 1:29:04  |
| After ignoring static mutants (120 timeouts)  | 3:32:17 | 3:22:11 | 1:32:39  |
| At `timeout-ratio` 4 (no timeouts)            | 2:25:12 | 3:17:11 | 1:02:57  |
| First scheduled run, 2026-10-16               | 2:16:08 | 1:49:22 | 1:10:37  |

The same mutants took 2:00:56 in part 2 of one run and 3:17:11 in another, with no timeouts in
either, so a hosted runner's speed varies by half again from one run to the next. Each part has a
300-minute timeout and Payments 180. Part 2 took longest in one of the two runs without timeouts
and finished first in the other, so neither part has a directory moved. Move one from part 2 to
part 1 if part 2 keeps taking longest.

Payments ran as one job until every mutant had a test. Then a whole local run tested 1,369 mutants
in 2:22:06, 6.2 s each, against 3.4 s for 928 mutants on 2026-10-14: more mutants are covered, and
each test process runs 520 tests instead of 164. Hosted runners took 1.2 to 1.75 times as long as
local runs for Payments, which puts a whole hosted run between 2:50 and 4:10, past the 180 minutes
its job had. So Payments runs in two parts too, each with 300 minutes: part 1 is `Processing/`, the
two engines, 864 of those mutants, and part 2 the other 505.

GitHub starts a scheduled run when a runner is free, not at the minute the cron names. The first
scheduled mutation run started at 04:35, more than four hours after 00:17, and in the same days the
06:23 Coverage (e2e) run started between 11:44 and 14:24. Every scheduled workflow here starts late
by about the same amount, so the mutation run still finished before that day's coverage run began,
and an earlier slot would not change that.

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
include the 51 mutants in static initializers, which all survived because the mutated value never
took effect (see [Static initializers](#static-initializers)). Ignoring those, as every later run
does, leaves **51.3%**: 572 killed, 333 survived and 211 not covered, so 544 undetected mutants
make the module's first survivor list. The run at `timeout-ratio` 4 gave exactly those counts, with
two [chance results](#chance-results) trading places. In the whole
hosted run Payments scored 54.0% again: 748 killed, 179 survived and 1 timed out. Of the 750 the
local run killed, one survived here and one timed out.

**Payments, with static mutants ignored: 54.3%**, from the hosted run on 2026-10-15. 743 killed,
167 survived, 1 timed out, 460 not covered, 121 compile errors, 186 ignored. Against the whole run,
the 17 static mutants are ignored and one more is killed: `op.DueAt > Now` made `>=` in
`PaymentEngine.ClaimAsync`, which survived the whole run while it shared a process with mutated
static fields, and fails a test when made by hand.

## Survivors worked through

Area by area, each in its own change. A module's gate comes on in the change that explains or
kills its last unexplained survivor.

| Area                                                         | Killed by new tests | Recorded as equivalent | Result, measured locally                                       |
| ------------------------------------------------------------ | ------------------: | ---------------------: | -------------------------------------------------------------- |
| Payments engines (`PaymentEngine.cs`, `SettlementEngine.cs`) |                 130 |                      8 | 99.1%: 841 killed, 8 survived, all explained, none not covered |
| Payments refund checks and MICR line                         |                 258 |                      8 | 97.0%: 258 killed, 8 survived, all explained, none not covered |
| The rest of Payments                                         |                 190 |                     33 | 87.1%: 219 killed, 4 timed out, 33 undetected, all explained   |

The engines' 138 undetected mutants, taken from the hosted run on 2026-10-15, are all accounted
for. `LeaseBook.Tests.Payments` grew from 164 tests to 273, among them 22 races (see
[A lock or a race](#a-lock-or-a-race)) and the fixture-binding guards (see
[A guard against a state no code makes](#a-guard-against-a-state-no-code-makes)).

The refund checks and the MICR line (`Domain/MicrLine.cs`, `Domain/RefundCheckRecords.cs` and
`Features/RefundChecks/`) had 266 undetected mutants in the first scheduled run, on 2026-10-16,
nearly all not covered because only the integration suite tested them. The pure MICR tests moved
from the integration suite to `LeaseBook.Tests.Payments`, and new tests there run the refund-check
commands and queries through the real dispatcher and validators, with the host's
`RefundCheckLedgerAdapter` and Accounting behind them. The suite grew from 273 tests to 407 and
runs in about 30 seconds. Of the eight equivalents, six are `= ""` defaults on `RefundCheck` that
every constructor overwrites, one negates a check-digit sum whose multiple-of-ten test is the same
either way, and one is the first `NumberTaken` throw in `IssueRefundCheck`, whose case the unique
index and the backstop below refuse the same way. That entry's text also matches the backstop's own
throw, which a race in `RefundCheckConcurrencyTests` kills; if that test ever stops killing it, the
entry would hide it.

The rest of Payments, the payment and settlement handlers and queries under `Features/`, the records
and the convenience fee in `Domain/`, and `Processing/IPaymentProcessor.cs`, had 223 undetected
mutants in the first scheduled run, and the same 223 when this work began. New tests in
`LeaseBook.Tests.Payments` dispatch the commands and queries through the real dispatcher, over the
same stand-in ports as the engine tests, and the convenience-fee tests moved there from the
integration suite with CsCheck. The suite grew from 407 tests to 520 and runs in about 34 seconds.
Of the 33 mutants left, 26 entries explain them: 17 entries cover 24 entity defaults, such as `= ""`
and `"USD"`, that every place making the row overwrites; four turn `Single` into `SingleOrDefault`
on a row the engine has just returned in the same transaction; and the other five are in the
lookups that join payments to their references, where the mutant changes which rows are read but
never what is shown. Some entity defaults read the same on two or three lines, and each such
entry names every line it covers, all equivalent.

With that, a whole local run of Payments scored **96.4%**: 1,318 killed, 47 survived, 4 timed out
and 2 not covered, with 121 compile errors and 186 ignored. All 49 undetected mutants are explained
and no entry is stale, so the Payments gate is on.
