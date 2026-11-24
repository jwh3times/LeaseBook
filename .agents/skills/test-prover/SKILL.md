---
name: test-prover
description: Prove that a change's new or changed tests catch what they were written for — run each one on its own and fail a run that matched no tests, put the base version of the fix back in a scratch worktree and confirm every new test goes red, break each changed guard one mutation at a time and confirm a test catches it, and check asserted names and strings against the source. Never changes the checkout it is called from. Use before committing a fix or feature that adds tests, in code review, and from open-pr when a branch adds tests.
argument-hint: "A base ref (default: the merge-base with origin/main), and optionally the tests to prove"
context: fork
model: inherit
background: false
metadata:
  settings: test-proof
---

# Test prover

A test that passes proves nothing until it has been seen to fail for the right reason. This skill
takes the tests a change adds or changes and shows, in a scratch copy of the repository, that each
one runs, goes red without the fix, and catches the fix's guards being broken. What it cannot
show, it reports.

**Announce at start:** "I'm using the test-prover skill to prove the new tests."

**Where the harness cannot fork.** This skill is written to run in a subagent of its own (Claude
Code reads `context: fork`, `model: inherit` and `background: false` above, and runs it on the
caller's model). A harness that loads it as an ordinary skill (Codex), or a caller that follows it
inline, runs every step below itself, unchanged: the only state it touches is a scratch worktree it
removes at the end. A subagent starts without the caller's conversation, so everything it needs
comes in the arguments: the base ref, the tests to prove when the caller knows them, and what the
change is meant to fix.

Run shell snippets in bash (Git Bash on Windows). `.agents/skills/test-prover/mutate.mjs` makes the
scratch worktree, applies one edit or revert at a time inside it, runs a test command, puts every
file back byte for byte and checks it, and prints one JSON object. Read its header once.

**Read-only for the caller.** Never edit, stage, commit or push in the checkout this skill was
called from, and never run `git checkout -- <path>`, `git restore`, `git stash`, `git reset` or
`git clean` in it or in the scratch worktree: every change goes through `mutate.mjs`, which puts it
back. Never fix a test or the code: report, and the caller decides.

## 0. Settings

This skill reads `docs/agents/test-proof.md`. Follow `.agents/skills/test-prover/settings/README.md`
to read it; the definitions sit beside it. Running as a subagent, no user can answer: a missing
setting with no default stops the run, and the report names it. A caller that can ask (`open-pr`, or a
session) completes this skill's settings first, by following this step, then runs it.

Then read the `Guide` in full, when it is not `none`: its traps are checked in step 6.

## 1. The change under proof

- **Base:** the ref in the arguments, or `git merge-base origin/main HEAD` after
  `git fetch -q origin`.
- **The change** is everything from the base to the checkout as it stands: its commits and its
  uncommitted work. A caller running this before its commit is covered, because `setup` copies the
  uncommitted work into the scratch worktree.
- **The tests:** those the arguments name, or else every test file the change adds or modifies,
  by the test file patterns in `Test commands` (`git diff --name-status "$base"` plus
  `git ls-files --others --exclude-standard`). In a modified test file, only the tests the diff adds
  or changes are under proof; name each by the name its runner filters on.
- **The fix:** every other changed file that is source code, leaving out docs, changelogs and
  fragments, lockfiles, generated files and test fixtures. A change with no fix files (tests
  added for existing behaviour) skips step 4 and proves by mutation alone (step 5).

No tests under proof: report that and stop.

## 2. The scratch worktree

```bash
node .agents/skills/test-prover/mutate.mjs setup --repo . --link node_modules
```

Pass one `--link` for each dependency folder `Test commands` names, and run each setup command it
names inside the worktree. Keep `root` from the answer: every command below takes it as
`--root "$root"`, and runs there. Step 7 removes it, whatever happens in between.

Each run below takes the test's single-test command from `Test commands` as `--command`, `--cwd`
for its folder, and `--zero-match` for each `Zero-match signature` line. The command runs in bash
on every machine (Git Bash on Windows, which `mutate.mjs` finds from git's own install), so write
it as any bash snippet: single quotes, `VAR=value command`, `&&`. A command that needs backslashes
goes in a script file the command runs instead, since the shell that calls `mutate.mjs` mangles
them. The `outcome` of a run is `red`, `green`, `zero-match`, `timeout` or `error`; read `tail`
for why. `error` means the command never ran or never finished on its own (it could not start,
the shell did not find it, a signal ended it): that run proves nothing either way, so the test or
mutation it was for is reported unproven, never killed. A file inside a folder `setup` linked is
never edited (`mutate.mjs` refuses it); a runner may still write its caches there.

## 3. Each test runs, on its own

For each test under proof:

```bash
node .agents/skills/test-prover/mutate.mjs run --root "$root" --cwd <folder> --command '<single-test command>'
```

It must be `green` with a `testCount` of at least 1. `testCount` is the tests that ran, passed or
failed; a skipped, todo or filtered-out test is not counted. `zero-match` means the filter or path
selected nothing, or every test it selected skipped itself (a missing service or variable): the
test as written never runs in that form, which is a finding, and the proof of that test stops
there. `red` means it fails with the fix in place: report it and go on with the others.
`error` means the command itself did not run: correct the command (and say which `Test commands`
entry is wrong), or report the test unproven. A `testCount` of `null` means the runner printed no
count this script reads: give it `--count`, or name the test as unproven for want of a count.

## 4. Red without the fix

```bash
node .agents/skills/test-prover/mutate.mjs revert --root "$root" --base "$base" --file <fix file> [--file <fix file> ...] --cwd <folder> --command '<single-test command>'
```

With every fix file put back as it was at the base (a file the fix added is removed for the run),
each new test must be `red`. One that stays `green` is vacuous: it passes with the bug in place. A
run that ends `error`, `timeout` or `zero-match` decides nothing: report that test unproven here.
Read the `tail` of a red run: an assertion failing on the behaviour the fix changed is the proof; a
test that fails only because it cannot load (an import the base lacks, a compile error) shows the
test needs the new code, not that it detects the bug. Report it as `red (load)`; step 5 then
carries its proof. With several fix files, revert each alone too when the combined revert does
not say which file a test depends on.

When the fix lives in the same file as its tests (a policy test that carries its own helper), a
whole-file revert removes the new tests too, and the run is `zero-match`. Put back only the fix's
lines instead, with `edit` (step 5's command), replacing the new code with what the base did (an
exemption that exempts nothing, a guard that was absent).

## 5. Break each guard

For each condition the fix adds or changes (an `if`, a loop or ternary condition, a boolean
`return`, a comparison, a regex, a path or name check, a filter), apply one mutation at a time and
run every test under proof against it:

- **negate** it (`ok` to `!ok`, `===` to `!==`);
- **drop one clause** of an `&&` or `||`, once for each clause;
- **swap a boundary** (`<` to `<=`, `>=` to `>`, an off-by-one in a slice or index);
- **weaken a constant** (an anchored regex unanchored, a path segment dropped, a returned `false`
  made `true`).

```bash
node .agents/skills/test-prover/mutate.mjs edit --root "$root" --file <fix file> --search '<exact text>' --replace '<mutated text>' --cwd <folder> --command '<single-test command>'
```

Text with quotes, backslashes or several lines goes in a JSON file (`{ "file", "search",
"replace" }`), written as a file rather than on the command line, and passed as `--edit <file>`.
The search must occur exactly once (`--occurrence <n>` picks one). A mutation is **killed** when
at least one test under proof goes `red` (a `timeout` counts, and is named); it **survives** when
they all stay `green`. One whose run ends `error` or `zero-match` is neither: name it untried. A
mutation that stops the code compiling or loading proves nothing: drop it and say so. Keep to the
guards the fix touches, twelve mutations at most; name the ones left untried.

A survivor is the finding that matters most: name the file and line, the mutation, and the input a
test would need to catch it.

## 6. Names, strings and traps

- **Names.** Each accessible name, label, text, message or key a new test asserts on
  (`getByRole("button", { name: "Save" })`, an error message, a log line, a JSON key) must appear,
  exactly, in the source it tests: search the non-test source for it. One found only in tests is a
  mismatch; a `queryBy...`-style assertion that it is absent then passes against anything.
- **Traps.** Check each new test against the `Guide`'s traps, and against these: a test skipped,
  `.only` left in, or behind a condition that is false in CI; an assertion inside a callback or
  loop that never runs; a mock or fixture that returns the very value asserted; a snapshot written
  by the same run; a `try` that swallows the assertion's failure; an assertion on a value that is
  always truthy (`toBeDefined` on a constructor's result).

## 7. Remove the worktree

```bash
node .agents/skills/test-prover/mutate.mjs teardown --root "$root"
```

Always, even when a step failed. A `teardown` error leaves a folder to remove by hand: name it.

## 8. Report

```markdown
## Test proof: <branch or "uncommitted work"> against <base>

| Test | Runs alone | Red without the fix | Mutations killed | Notes |
| --- | --- | --- | --- | --- |
| <file> › <name> | yes, 1 test / zero-match / red | yes / red (load) / no: vacuous | 3 of 4 | <the survivor, a name mismatch, a trap> |

**Survivors:** <file:line — the mutation — the input a test would need>, or none.
**Zero-match runs:** <command — what it printed>, or none.
**Names:** <asserted text not found in the source>, or all found.
**Not proven:** <tests that could not run here, and why>.
**Verdict:** proven, or not proven: <the reasons, in one line each>.
```

Then the settings that were missing, if any. The caller decides what to change: a vacuous test or a
survivor is fixed by a better test, never by weakening the mutation.
