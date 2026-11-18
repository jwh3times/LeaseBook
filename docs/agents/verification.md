# Verification

- **Audience:** Coding agents and maintainers
- **Status:** Living policy
- **Owner:** Maintainers
- **Last reviewed:** 2026-11-18

How to prove a test, a guard or a claim before trusting it. `AGENTS.md` says "verify, do not
assume"; this file holds the ways a check here has passed while proving nothing. Write the guard
first and prove it goes red.

## Prove the guard goes red

- **Assert the mutation applied.** A `sed` that silently failed to match once reported two
  mutations as surviving that had already been proven caught. Count the pattern before and after,
  and grep the mutated line for the exact text, not only that it changed.
- **The mutation must compile.** The build treats warnings as errors, so `if (false) { … }` fails
  with CS0162, and running the already-built test exe then exercises the old binaries. Use a
  condition the compiler can't fold, such as `if (DateTime.UtcNow.Year < 0)`, and run the tests only
  when the build succeeded.
- **Restoring a file with `cp` or `Copy-Item` keeps the backup's mtime**, so MSBuild skips the
  rebuild and the next run tests the mutated build. `touch` the file or build with
  `--no-incremental`.
- **Reverting a deletion with `str.replace('', original)` inserts at the start of the file.**
  Restore from `git diff` or an anchor, then rebuild.
- **End a guard-removal harness with an unmodified baseline run**, and print each run's total beside
  its failures (see [local runs](local-runs.md#net-test-runs) for filters that match nothing).

## Guards that can't see what they guard

- **A check can be unfalsifiable by construction.** Before adding a monitoring assertion, name the
  data state that makes it red. If none exists, it stays green forever and looks like coverage
  (ADR-016's 2026-09-09 addendum).
- **A guard can be scoped so it can't see the cases that matter.** `ErrorContractTests` scans for
  direct `Results.Problem` calls, so it says nothing about error responses written by middleware or
  framework events, which is where a bare 401 hid for months. When a guard enforces "everything goes
  through X", ask what reaches the user without going through X, and guard that on a second axis
  (`MiddlewareErrorContractTests`).
- **A LINQ check on a context with no org id returns nothing.** The org-scoped global query filter
  compares against the context's org id, so a swept invariant written in LINQ passes vacuously. Swept
  invariants and diagnostics reads in `InvariantChecks` use raw SQL, which RLS still scopes, and each
  one's planted-breach test must go red first.
- **"Some geometry exists" is unfalsifiable on a PDF page**: rules and borders are vector paths too.
  Assert an exact delta from a baseline.
- **A scrubber and a check that share their patterns are one opinion.** Build the independent check
  from a literal denylist of the raw values themselves, and have a reviewer attack it with realistic
  payloads.
- **A "never echoes the secret" assertion must not match random ids.** Strip `correlationId` and
  `traceId`, compare runs of four or more digits, and prove it fails by making the validator echo
  the value.
- **A green run says nothing about what that run excludes.** Before dating a recovery from run
  history, check `git log -S<url> -- <workflow>` and the run's event: exclusions differ between a
  sweep, a PR and a push.

## Tests that pass for the wrong reason

- **A discovery-driven e2e can be tautological.** A leg that read "the first unit" from the page
  read the Tenants table, which also has a `Unit` column, and passed with the fix reverted. Scope by
  a card's heading, assert the row label exactly, and revert the fix to prove the test.
- **A one-of-a-kind fixture proves only that a value exists.** With one property seeded,
  `unit.PropertyId == property.Id` passes against any join. Seed two, assert each maps to its own,
  and falsify with a wrong-but-valid value, not only null.
- **Prefer a distinctive pinned fixture value over "the first row"** when the seed has duplicates:
  the demo org has three units labelled `#1`. Check list orderings for a missing tiebreak.
- **A test that pins copy the client invented can't see that the server was never consulted.**
  Assert the server's string and its correlation reference. When deleting invented copy, grep
  `web/e2e/` as well as the unit tests.
- **Bound a symmetric property on both sides.** A one-sided floor passed a regression that made one
  arm twice as expensive.
- **Reusing one account across failed logins stops exercising the hasher**: past five failures
  sign-in returns `LockedOut` without verifying the password. Use a fresh account per sample, and
  check the expensive path is really running before trusting a null result.
- **Don't infer what already happened from a result enum.** `SignInResult.LockedOut` comes both
  from the pre-check and from the attempt that trips the lockout, which already ran the hasher.
  Predict the branch before the call.
- **Shouldly overloads that bind to the wrong thing:** `ShouldHaveSingleItem()` takes a message, not
  a predicate; `s.ShouldContain("a", "msg")` resolves to `IEnumerable<char>`; the third parameter of
  `ShouldStartWith` is a `Case`. Worst, `nullable?.Value.ShouldBe(x, "msg")` passes when the value is
  absent: assert `ShouldNotBeNull()` first. When an overload takes a third argument, check what it
  is.
- **Renaming an accessible name leaves absence checks passing vacuously.** Testing Library matches
  the whole name and Playwright a substring, so after a rename `queryByRole(…, { name: 'Old' })`
  plus `not.toBeInTheDocument()` passes either way. Anchor absence checks (`/^Post return/`).
- **A source-scanning test reads its own file**, doc comments included. Exclude `*.test.ts(x)` from
  the walk.
- **A new test that is green before its fix exists** is the signal. Never accept it as "already
  covered" without finding out why.

## Claims

- **Open the file the finding cites.** A repo-wide grep "confirmed" two security fixes by hitting a
  sibling resource (a different vault, a different cookie). Confirm the property the finding
  protects, not its proposed patch: a fix may land by another mechanism.
- **Before reasoning about framework internals from memory, grep for a test that pins the
  behavior.** `WebApplicationFactory` does run everything after `builder.Build()`, and two existing
  tests prove it.
- **Verify an issue's verification recipe, not just its list.** #349 named nine surfaces when there
  were 19, because its `rg` recipe subtracted at file level.
- **Verify supplied artifacts against their bytes.** An icon pack's README described a seven-size ICO
  that held one 16×16 entry and an "SVG" that was a base64 PNG.
- **Derive durations, counts and superlatives; never recall them.** "For years" and "the one X in
  the product" go stale silently.
- **`Gate = Trigger-fired` means gated on a trigger, not that it fired.** Establish a trigger's state
  by reading its condition against the world.
- **When you add a rule to a guidance file, grep that file's own examples against it.** A canonical
  example forty lines above a new rule still taught the forbidden pattern.
- **After editing a normative statement, grep the whole record for other copies of the claim.**
- **When adding a refusal, open the screen it sends the user to.** One told operators to fill a
  field Settings couldn't edit.
