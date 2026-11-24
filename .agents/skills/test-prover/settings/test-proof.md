<!-- Generated from settings/test-proof.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: test-proof

`docs/agents/test-proof.md` — how `test-prover` runs one test on its own in this repository, and how
it tells a run that tested nothing from a pass. Each setting below is a `##` heading in that file.

## Test commands

- **Read by:** test-prover
- **Value:** bulleted list, one per stack or test suite:
  - the folder it runs from and which files are its tests (`web/`: `src/**/*.test.ts`);
  - the command that runs one test file, with `<file>` where its path goes, and one test by name
    where the runner can, with `<name>` (`npx vitest run <file> -t "<name>"`). It is written for
    bash, which `mutate.mjs` runs it in on every machine (Git Bash on Windows), so the same line
    works everywhere; with `go test`, include `-v`, which prints the per-test lines the count
    comes from;
  - the line of output that gives the number of tests run (`Tests  3 passed (3)`), when it is not
    one `mutate.mjs` reads already: node's test runner, Vitest, Jest, Playwright, `dotnet test`
    (VSTest and Microsoft Testing Platform), pytest's closing summary, `go test -v`, `cargo test`,
    `deno test` and Python's `unittest` (it reads `--count`, a regex written without backslashes,
    for any other). It counts only the tests that ran, passed or failed, never one skipped, todo,
    filtered out or deselected, so a by-name run counts the one test, not the file's total;
  - what a scratch worktree needs before the command can run: dependency folders to link from the
    checkout (`node_modules`, `web/node_modules`), or a command (`dotnet restore`, `go mod
    download`); `none` when it needs nothing.

  A suite that cannot run on one file or one test (it needs a service, a browser or a deployed
  stack the machine lacks) is named with the reason, and `test-prover` reports its tests as not
  proven rather than running them.
- **Discover:** the `Fast checks` in `docs/agents/open-pr.md`, the `test` scripts in each
  `package.json` (and `*.csproj`, `go.mod`, `pyproject.toml`), the test jobs in
  `.github/workflows/`, and the repository's testing docs. Run one existing test with the proposed
  command to confirm it runs exactly that test and prints its count.

## Zero-match signature

- **Read by:** test-prover
- **Value:** `none`, or bulleted lines of output that mean a run selected no tests at all, beyond
  the ones `mutate.mjs` knows (`No test files found`, `No test suite found`, `No tests found`, `no
  tests ran`, `no tests to run`, `[no test files]`, `[no tests to run]`, `No test matches the given
  testcase filter`, `No test is available`, `Could not find '` (node's runner given a file that
  does not exist), a count of 0 tests run, which is what a filter that matches nothing or a run in
  which every test skipped itself reads, and node's runner reporting the test file itself as the
  one passing test, which it does when `--test-name-pattern` matches nothing in it). Each is plain text in any case, matched where a runner prints it: at the start
  of a line (after leading marks, `Error:` or `testing: warning:`), or at the end of one for a
  bracketed line, never inside a failure message. A failing run that counted its tests is red
  whatever it printed.
- **Default:** `none`: the built-in signatures.
- **Discover:** run a single-test command from `Test commands` with a filter that matches nothing,
  and see what it prints and how it exits. Write the heading only when it prints none of the
  built-in lines.

## Guide

- **Read by:** test-prover
- **Value:** `none`, or the path of this repository's own account of how its tests can pass without
  proving anything (mocks that answer the assertion, a skipped suite, a fixture that hides the
  bug, a check CI runs that a local run does not), which `test-prover` reads in full every run and
  checks the new tests against.
- **Default:** `docs/agents/verification.md` when it exists; otherwise `none`.
- **Discover:** the testing or verification document `AGENTS.md` points to, and the "false green"
  or "vacuous test" lists in `docs/agents/`, `docs/` and `private/`. Write the heading only when it is
  another file.
