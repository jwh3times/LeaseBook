import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  classifyEdit,
  classifyGh,
  classifyGit,
  decide,
  emptySettings,
  evaluate,
  parseGuardSettings,
  parsePayload,
  render,
  repoRoot,
  resolveDirs,
  splitCommands,
  windowsWarnings,
} from "./guard.mjs";

// Node's own `import.meta.dirname`; a runner that leaves it unset (Vitest gives `import.meta.url` a
// non-file scheme) runs from the repository root, where the scripts are in `scripts/`.
const guardPath = path.join(import.meta.dirname ?? path.resolve("scripts"), "guard.mjs");

// Every side effect the guard has, faked: files and folders by path, and git answers by
// subcommand (`status` gives porcelain output, `rev-parse` the branch; missing means git failed).
function fakeCtx({ platform = "linux", root = "/repo", files = {}, dirs = [], git = {} } = {}) {
  const key = (p) => (platform === "win32" ? p.toLowerCase() : p);
  const fileMap = new Map(Object.entries(files).map(([p, content]) => [key(p), content]));
  const dirSet = new Set(dirs.map(key));
  const calls = [];
  return {
    platform,
    home: platform === "win32" ? "C:/Users/me" : "/home/me",
    root,
    cwd: root,
    calls,
    exists: (p) => fileMap.has(key(p)) || dirSet.has(key(p)),
    readFile: (p) => fileMap.get(key(p)) ?? null,
    git: (dir, args) => {
      calls.push(`${dir}: git ${args.join(" ")}`);
      return git[args[0]] ?? null;
    },
  };
}

const repo = (extra = {}) => ({ dirs: ["/repo/.git", ...(extra.dirs ?? [])], ...extra, files: { "/repo/scripts/sync-agents.mjs": "", ...extra.files } });
const CLEAN = { status: "", "rev-parse": "feat/x\n" };
const DIRTY = { status: " M src/a.js\n?? notes.txt\n", "rev-parse": "feat/x\n" };

function bash(command, options = {}) {
  const ctx = fakeCtx(repo(options));
  return evaluate({ tool: options.tool ?? "Bash", input: { command }, cwd: options.cwd ?? "/repo" }, ctx);
}

const rules = (result) => [...(result?.reason ?? "").matchAll(/\[guard (\w+)\]/g)].map((m) => m[1]);
const outcome = (result) => (result ? (result.decision ?? "warn") : null);

// --- Tokenizer -----------------------------------------------------------------------------------

test("the tokenizer splits on separators and newlines, and respects quotes", () => {
  const words = (text, shell) => splitCommands(text, shell).map((c) => c.words.join(" "));
  const cases = [
    ["git status; git log && npm test || echo no", ["git status", "git log", "npm test", "echo no"]],
    ["a | b & c\nd", ["a", "b", "c", "d"]],
    [`echo "a; b && c" 'd | e'`, ["echo a; b && c d | e"]],
    [`git commit -m "it's \\"quoted\\""`, [`git commit -m it's "quoted"`]],
    ["FOO=1 BAR=2 env -u X sudo -u me git push", ["git push"]],
    ["git status 2>&1 >/dev/null | tee out.log", ["git status", "tee out.log"]],
    ["git log >out.txt 2>err.txt; echo x &>/dev/null", ["git log", "echo x"]],
    ["ls # git reset --hard", ["ls"]],
    ["if true; then git reset --hard; fi", ["true", "git reset --hard", "fi"]],
  ];
  for (const [text, expected] of cases) assert.deepEqual(words(text), expected, text);
});

test("heredoc bodies are skipped unless a shell reads them", () => {
  const programs = (text) => splitCommands(text).map((c) => c.words.join(" "));
  assert.deepEqual(programs("cat <<'EOF' > f.txt\ngit reset --hard\nEOF\necho done"), ["cat", "echo done"]);
  assert.deepEqual(programs("cat <<-EOF\n\tgit push -f\n\tEOF\n"), ["cat"]);
  assert.deepEqual(programs("bash <<EOF\ngit push -f\nEOF\n"), ["bash", "git push -f"]);
  assert.deepEqual(programs("sh -s <<< 'git branch -D x'"), ["sh -s", "git branch -D x"]);
  const [cat] = splitCommands("cat <<EOF\na\nb\nEOF\n");
  assert.deepEqual([cat.heredocs[0].lines, cat.heredocs[0].body], [2, "a\nb"]);
});

test("bash -c, eval, substitutions and pwsh -Command are checked as commands too", () => {
  const programs = (text, shell) => splitCommands(text, shell).map((c) => c.words.join(" "));
  assert.ok(programs("bash -lc 'git push --force'").includes("git push --force"));
  assert.ok(programs("eval git branch -D old").includes("git branch -D old"));
  assert.ok(programs('echo "$(git reset --hard)"').includes("git reset --hard"));
  assert.ok(programs("echo `git clean -fdx`").includes("git clean -fdx"));
  assert.ok(programs(`powershell.exe -NoProfile -Command "git reset --hard"`).includes("git reset --hard"));
  const encoded = Buffer.from("git push -f", "utf16le").toString("base64");
  assert.ok(programs(`pwsh -EncodedCommand ${encoded}`).includes("git push -f"));
});

test("PowerShell: quotes, the call operator, backtick escapes and redirection", () => {
  const programs = (text) => splitCommands(text, "powershell").map((c) => c.words.join(" "));
  assert.deepEqual(programs("cd C:\\dev\\x; git status 2>&1 | Out-Null"), ["cd C:\\dev\\x", "git status", "Out-Null"]);
  assert.deepEqual(programs("git commit -m 'it''s' -m \"say `\"hi`\"\""), [`git commit -m it's -m say "hi"`]);
  assert.ok(programs("& 'C:/Program Files/Git/bin/bash.exe' -lc 'git push origin +main'").includes("git push origin +main"));
  assert.deepEqual(programs("Get-ChildItem | ForEach-Object { git checkout -- $_ }").at(-1), "git checkout -- $_");
});

test("text that cannot be parsed asks only when it mentions something risky", () => {
  assert.throws(() => splitCommands('echo "open'), /unterminated double quote/);
  assert.throws(() => splitCommands("cat <<EOF\nno end"), /heredoc without its closing EOF/);
  assert.equal(bash('echo "open'), null);
  const result = bash("git push --force 'open");
  assert.equal(result.decision, "ask");
  assert.match(result.reason, /Could not parse/);
});

// --- Directories ---------------------------------------------------------------------------------

test("cd and git -C move the directory a git command acts in; a subshell's cd ends with it", () => {
  const dirs = (text, cwd = "/repo", shell = "bash") =>
    resolveDirs(splitCommands(text, shell), cwd, { platform: "linux", home: "/home/me" });
  assert.deepEqual(dirs("cd sub && git status"), ["/repo/sub", "/repo/sub"]);
  assert.deepEqual(dirs("git -C ../other -C x status"), ["/other/x"]);
  assert.deepEqual(dirs("(cd a && git status); git status"), ["/repo/a", "/repo/a", "/repo"]);
  assert.deepEqual(dirs("cd $X; git status"), [null, null]);
  assert.deepEqual(dirs("cd -; git status"), [null, null]);
  assert.deepEqual(dirs("cd; git status"), ["/home/me", "/home/me"]);
  assert.deepEqual(dirs("cd /abs; git status", null), ["/abs", "/abs"]);
  // Unquoted, bash eats the backslash (`..\y` is `..y`), so a Windows path is quoted.
  const win = resolveDirs(splitCommands("cd /c/dev/x && git -C '..\\y' status"), "C:\\dev\\skills", { platform: "win32" });
  assert.deepEqual(win, ["C:/dev/x", "C:/dev/y"]);
  const ps = resolveDirs(splitCommands("Set-Location -Path sub; git status", "powershell"), "C:\\dev\\skills", { platform: "win32" });
  assert.deepEqual(ps, ["C:/dev/skills/sub", "C:/dev/skills/sub"]);
});

test("the repository root is the nearest folder holding .git, folder or worktree file", () => {
  const ctx = fakeCtx({ files: { "/wt/feature/.git": "gitdir: /repo/.git/worktrees/feature" }, dirs: ["/repo/.git"] });
  assert.equal(repoRoot("/repo/a/b", ctx), "/repo");
  assert.equal(repoRoot("/wt/feature/src", ctx), "/wt/feature");
  assert.equal(repoRoot("/elsewhere", ctx), null);
  const win = fakeCtx({ platform: "win32", dirs: ["C:/dev/skills/.git"] });
  assert.equal(repoRoot("c:/Dev/Skills/x", win).toLowerCase(), "c:/dev/skills");
});

// --- Push and merge ------------------------------------------------------------------------------

test("push: force denies, main and deletes ask, a feature branch passes", () => {
  const cases = [
    ["git push --force", "feat/x", ["D5"]],
    ["git push -f origin feat/x", "feat/x", ["D5"]],
    ["git push -uf origin feat/x", "feat/x", ["D5"]],
    ["git push --force-with-lease=feat/x origin feat/x", "feat/x", ["D5"]],
    ["git push --mirror", "feat/x", ["D5", "A1"]],
    ["git push origin +feat/x", "feat/x", ["D5"]],
    ["git push origin main", "feat/x", ["A1"]],
    ["git push origin HEAD:refs/heads/master", "feat/x", ["A1"]],
    ["git push --all origin", "feat/x", ["A1"]],
    ["git push", "main", ["A1"]],
    ["git push origin HEAD", "main", ["A1"]],
    ["git push --delete origin old", "feat/x", ["A2"]],
    ["git push origin -d old", "feat/x", ["A2"]],
    ["git push origin :old", "feat/x", ["A2"]],
    ["git push -u origin feat/x", "main", []],
    ["git push", "feat/x", []],
    ["git push origin --tags", "main", []],
    ["git push --no-force-with-lease origin feat/x", "feat/x", []],
  ];
  for (const [command, branch, expected] of cases) {
    const found = classifyGit(command.split(" ").slice(1), { dir: "/repo", branch: () => branch });
    assert.deepEqual(found.map((f) => f.rule), expected, command);
  }
  const unknown = classifyGit(["push"], { dir: null, branch: () => null });
  assert.deepEqual(unknown.map((f) => [f.level, f.rule]), [["ask", "A5"]]);
});

test("gh pr merge with --admin, --squash, --rebase or --auto is denied", () => {
  const cases = [
    ["pr merge 12 --admin", true],
    ["pr merge 12 --squash", true],
    ["pr merge 12 -s", true],
    ["pr merge 12 -r --delete-branch", true],
    ["pr merge --auto --merge 12", true],
    ["-R o/r pr merge 12 --merge", false],
    ["pr merge 12 --merge -b squash", false],
    ["pr view 12 --squash", false],
  ];
  for (const [args, denied] of cases) {
    assert.equal(classifyGh(args.split(" ")).some((f) => f.rule === "D6"), denied, args);
  }
});

// --- Dirty-gated commands ------------------------------------------------------------------------

test("each discarding command asks when the tree is dirty and passes when it is clean", () => {
  const gated = [
    "git checkout -- src/a.js",
    "git checkout .",
    "git checkout -f main",
    "git checkout main src/a.js",
    "git checkout src",
    "git restore src/a.js",
    "git restore --staged --worktree src/a.js",
    "git reset --hard",
    "git reset --hard HEAD~1",
    "git switch --discard-changes main",
  ];
  for (const command of gated) {
    assert.deepEqual(rules(bash(command, { git: DIRTY, dirs: ["/repo/src"] })), ["A4"], `dirty: ${command}`);
    assert.equal(bash(command, { git: CLEAN, dirs: ["/repo/src"] }), null, `clean: ${command}`);
  }
});

test("switching branches, unstaging and a soft reset pass on a dirty tree without a git call", () => {
  for (const command of ["git checkout main", "git checkout -b feat/y origin/main", "git restore --staged src/a.js", "git restore -S x", "git reset HEAD~1", "git status"]) {
    const ctx = fakeCtx(repo({ git: DIRTY }));
    assert.equal(evaluate({ tool: "Bash", input: { command }, cwd: "/repo" }, ctx), null, command);
    if (!command.startsWith("git checkout")) assert.deepEqual(ctx.calls, [], command);
  }
});

test("git clean: -x always asks; otherwise only untracked files gate it", () => {
  assert.deepEqual(rules(bash("git clean -fdx", { git: CLEAN })), ["A4c"]);
  assert.deepEqual(rules(bash("git clean -X -f", { git: CLEAN })), ["A4c"]);
  assert.equal(bash("git clean -ndx", { git: CLEAN }), null);
  assert.deepEqual(rules(bash("git clean -fd", { git: DIRTY })), ["A4"]);
  assert.equal(bash("git clean -fd", { git: { status: " M a.js\n" } }), null);
});

test("a git state it cannot read asks (A5), and the dirty check runs where cd and -C point", () => {
  assert.deepEqual(rules(bash("git reset --hard", { git: {} })), ["A5"]);
  assert.deepEqual(rules(bash("cd $SOMEWHERE && git reset --hard", { git: CLEAN })), ["A5"]);
  const ctx = fakeCtx(repo({ git: CLEAN }));
  evaluate({ tool: "Bash", input: { command: "cd sub && git -C private reset --hard" }, cwd: "/repo" }, ctx);
  assert.deepEqual(ctx.calls, ["/repo/sub/private: git status --porcelain --untracked-files=normal"]);
});

test("git branch -D asks; -d does not", () => {
  assert.deepEqual(rules(bash("git branch -D old")), ["A3"]);
  assert.deepEqual(rules(bash("git branch --delete --force old")), ["A3"]);
  assert.equal(bash("git branch -d old"), null);
});

// --- Edits ---------------------------------------------------------------------------------------

const AGENTS = [
  "# Rules",
  "",
  "Our own rule.",
  "",
  "<!-- shared:flow -->",
  "Shared text.",
  "<!-- /shared:flow -->",
  "",
  "Shared text.",
  "",
].join("\n");

function edit(tool, input, options = {}) {
  const ctx = fakeCtx(repo(options));
  return evaluate({ tool, input, cwd: "/repo" }, ctx);
}

test("edits to generated mirrors and lock-listed installs are denied, by any spelling of the path", () => {
  const lock = JSON.stringify({ version: 1, skills: { ship: { source: "jwh3times/skills" } } });
  const files = { "/repo/skills-lock.json": lock };
  assert.deepEqual(rules(edit("Edit", { file_path: "/repo/.claude/skills/ship/SKILL.md" })), ["D1"]);
  assert.deepEqual(rules(edit("Write", { file_path: ".codex/agents/a.toml" })), ["D2"]);
  assert.deepEqual(rules(edit("MultiEdit", { file_path: "/repo/.agents/skills/ship/SKILL.md", edits: [] }, { files })), ["D3"]);
  assert.equal(edit("Edit", { file_path: "/repo/.agents/skills/local/SKILL.md" }, { files }), null);
  assert.equal(edit("Edit", { file_path: "/repo/skills/ship/SKILL.md" }, { files }), null);

  const win = fakeCtx({
    platform: "win32",
    dirs: ["C:/dev/skills/.git"],
    files: { "C:/dev/skills/scripts/sync-agents.mjs": "", "C:/dev/skills/skills-lock.json": lock },
  });
  const winEdit = (file_path) => evaluate({ tool: "Edit", input: { file_path }, cwd: "C:\\dev\\skills" }, win);
  assert.deepEqual(rules(winEdit("C:\\dev\\skills\\.Claude\\Skills\\ship\\SKILL.md")), ["D1"]);
  assert.deepEqual(rules(winEdit("c:\\DEV\\skills\\.agents\\skills\\Ship\\x.md")), ["D3"]);
});

test("a worktree's root is its own, and a repository that generates no mirrors is left alone", () => {
  const ctx = fakeCtx({
    files: { "/wt/f/.git": "gitdir: x", "/wt/f/scripts/sync-agents.mjs": "" },
    dirs: ["/repo/.git"],
  });
  assert.deepEqual(rules(evaluate({ tool: "Edit", input: { file_path: "/wt/f/.claude/skills/a/SKILL.md" } }, ctx)), ["D1"]);
  const plain = fakeCtx({ dirs: ["/other/.git"] });
  assert.equal(evaluate({ tool: "Edit", input: { file_path: "/other/.claude/skills/a/SKILL.md" } }, plain), null);
  assert.equal(evaluate({ tool: "Edit", input: { file_path: "/home/me/.claude/skills/a/SKILL.md" } }, plain), null);
});

test("an Edit overlapping a shared block in AGENTS.md is denied; one outside it passes", () => {
  const files = { "/repo/AGENTS.md": AGENTS };
  const agentsEdit = (input, tool = "Edit", extra = files) =>
    rules(edit(tool, { file_path: "/repo/AGENTS.md", ...input }, { files: extra }));
  assert.deepEqual(agentsEdit({ old_string: "Our own rule.", new_string: "x" }), []);
  assert.deepEqual(agentsEdit({ old_string: "<!-- shared:flow -->\nShared", new_string: "x" }), ["D4"]);
  assert.deepEqual(agentsEdit({ old_string: "rule.\n\n<!-- sha", new_string: "x" }), ["D4"]);
  assert.deepEqual(agentsEdit({ old_string: "-->\n\nShared text.", new_string: "x" }), ["D4"]);
  // Not unique: with replace_all every occurrence counts, and one is inside the block.
  assert.deepEqual(agentsEdit({ old_string: "Shared text.", new_string: "x", replace_all: true }), ["D4"]);
  assert.deepEqual(agentsEdit({ edits: [{ old_string: "Our own", new_string: "x" }, { old_string: "Shared text.\n<!--", new_string: "y" }] }, "MultiEdit"), ["D4"]);
  assert.deepEqual(agentsEdit({ old_string: "Shared text.", new_string: "x" }, "Edit", { "/repo/AGENTS.md": AGENTS.replaceAll("\n", "\r\n") }), ["D4"]);
  assert.deepEqual(agentsEdit({ old_string: "Our own\r\nrule", new_string: "x" }, "Edit", { "/repo/AGENTS.md": AGENTS.replace("own rule", "own\r\nrule") }), []);
});

test("a Write to AGENTS.md is denied only when it changes a shared block", () => {
  const files = { "/repo/AGENTS.md": AGENTS };
  const write = (content) => rules(edit("Write", { file_path: "/repo/AGENTS.md", content }, { files }));
  assert.deepEqual(write(AGENTS.replace("Our own rule.", "A new rule.")), []);
  assert.deepEqual(write(AGENTS.replace("Shared text.\n<!-- /", "Edited.\n<!-- /")), ["D4"]);
  assert.deepEqual(write("# Rules\n"), ["D4"]);
  assert.deepEqual(rules(edit("Write", { file_path: "/repo/AGENTS.md", content: "# New\n" })), []);
});

// --- Repository rules ----------------------------------------------------------------------------

const GUARD_MD = [
  "# Guard",
  "",
  "Prose under the title is someone else's.",
  "",
  "## Deny commands",
  "",
  "- `supabase db reset` — it wipes the local database,",
  "  seeded by hand.",
  "- `^docker volume (rm|prune)` — volumes hold the data.",
  "",
  "## Ask commands",
  "",
  "- `^supabase db push$` — a bare push targets the linked project.",
  "",
  "## Protected paths",
  "",
  "- `^\\.env(\\..*)?$` — local secrets.",
  "- `^private/$` — the companion checkout.",
  "",
  "## Notes",
  "",
  "Anything at all.",
].join("\n");

test("guard.md parses its three headings, continuations and none; other headings are ignored", () => {
  const settings = parseGuardSettings(GUARD_MD);
  assert.deepEqual(settings.deny.map((r) => [r.pattern, r.reason, r.line]), [
    ["supabase db reset", "it wipes the local database, seeded by hand.", 7],
    ["^docker volume (rm|prune)", "volumes hold the data.", 9],
  ]);
  assert.equal(settings.ask.length, 1);
  assert.equal(settings.protected.length, 2);
  assert.deepEqual(parseGuardSettings("## Deny commands\n\nnone\n"), emptySettings());
  assert.deepEqual(parseGuardSettings("# Guard\r\n"), emptySettings());
});

test("a malformed guard.md throws, naming the line", () => {
  const bad = [
    ["## Deny commands\n\n- supabase db reset — no backticks\n", /guard\.md:3: expected `none` or a bullet/],
    ["## Deny commands\n\n- `(unclosed` — bad\n", /guard\.md:3: invalid regex/],
    ["## Ask commands\n\nnone\n- `x` — y\n", /guard\.md:4: '## Ask commands' says `none`/],
    ["## Ask commands\n\n## Deny commands\nnone\n", /guard\.md:1: '## Ask commands' has no value/],
    ["## Ask commands\nnone\n## Ask commands\nnone\n", /guard\.md:3: '## Ask commands' appears twice/],
    ["## Protected paths\n\nSome prose.\n", /guard\.md:3:/],
  ];
  for (const [text, pattern] of bad) assert.throws(() => parseGuardSettings(text), pattern, text);
});

test("repository rules deny, ask and protect paths, matched on the command with prefixes stripped", () => {
  const files = { "/repo/docs/agents/guard.md": GUARD_MD, "/repo/.env": "SECRET=1" };
  assert.deepEqual(rules(bash("npx supabase db reset", { files })), ["R"]);
  assert.equal(bash("npx supabase db reset", { files }).decision, "deny");
  assert.equal(bash("FOO=1 docker.exe volume prune -f", { files }).decision, "deny");
  assert.equal(bash("supabase db push", { files }).decision, "ask");
  assert.equal(bash("supabase db push --local", { files }), null);
  assert.equal(bash("rm -f .env", { files }).decision, "ask");
  assert.equal(bash("cd private/.. && rm -rf private", { files }).decision, "ask");
  assert.equal(bash("rm -rf dist", { files }), null);
  assert.equal(edit("Write", { file_path: "/repo/.env", content: "" }, { files }).decision, "ask");
  assert.equal(edit("Edit", { file_path: "/repo/.env", old_string: "1", new_string: "2" }, { files }), null);
  const unparsed = bash("supabase db reset 'open", { files });
  assert.equal(unparsed.decision, "ask");
});

// --- Windows warnings ----------------------------------------------------------------------------

test("each Windows warning fires only for the Bash tool on win32", () => {
  const cases = [
    ["W1", "node -e \"console.log('a\\\\b')\""],
    ["W2", `cat <<'EOF' > x.md\nuse \`code\`\nEOF\n`],
    ["W2", `cat <<'EOF' > x.md\n${"line\n".repeat(101)}EOF\n`],
    ["W3", "gh api x | jq .name"],
    ["W4", 'gh pr create --title t --body "Some prose"'],
    ["W5", "grep -c $'\\x00' file.txt"],
    ["W6", "python script.py"],
  ];
  for (const [rule, command] of cases) {
    const found = splitCommands(command).flatMap((c) => windowsWarnings(c, { platform: "win32", tool: "Bash" }));
    assert.deepEqual(found.map((f) => f.rule), [rule], command);
    assert.deepEqual(splitCommands(command).flatMap((c) => windowsWarnings(c, { platform: "linux", tool: "Bash" })), [], command);
    assert.deepEqual(splitCommands(command).flatMap((c) => windowsWarnings(c, { platform: "win32", tool: "PowerShell" })), [], command);
  }
  for (const command of ["node -e \"console.log(1)\"", "gh pr create --body-file b.md", "grep -cP '\\x00' f", ".venv/Scripts/python x.py", "cat <<EOF\nshort\nEOF\n"]) {
    const found = splitCommands(command).flatMap((c) => windowsWarnings(c, { platform: "win32", tool: "Bash" }));
    assert.deepEqual(found, [], command);
  }
});

test("a warning alone is context with no permission decision", () => {
  const ctx = fakeCtx({ platform: "win32", root: "C:/repo", dirs: ["C:/repo/.git"] });
  const result = evaluate({ tool: "Bash", input: { command: "gh api x | jq ." }, cwd: "C:\\repo" }, ctx);
  assert.equal(outcome(result), "warn");
  const out = JSON.parse(render(result));
  assert.deepEqual(Object.keys(out.hookSpecificOutput).sort(), ["additionalContext", "hookEventName"]);
});

// --- Output --------------------------------------------------------------------------------------

test("deny beats ask, warnings ride along, and allow is never emitted", () => {
  const f = (level, rule) => ({ level, rule, reason: rule });
  assert.equal(decide([f("ask", "A1"), f("deny", "D5"), f("warn", "W3")]).decision, "deny");
  assert.equal(decide([f("ask", "A1"), f("warn", "W3")]).decision, "ask");
  assert.equal(decide([f("ask", "A1"), f("ask", "A1")]).reason, "[guard A1] A1");
  assert.equal(decide([]), null);
  const both = JSON.parse(render(decide([f("deny", "D5"), f("warn", "W3")])));
  assert.deepEqual(both, {
    hookSpecificOutput: {
      hookEventName: "PreToolUse",
      permissionDecision: "deny",
      permissionDecisionReason: "[guard D5] D5",
      additionalContext: "[guard W3] W3",
    },
  });
  assert.equal(render(null), "");
  assert.equal(render({ decision: "allow", reason: "x", context: null }), "");
  assert.equal(render({ decision: "ask", reason: "x", context: null }, "PostToolUse"), "");
  for (const command of ["git push --force", "git status", "gh pr merge 1 --admin", "rm -rf /"]) {
    assert.doesNotMatch(render(bash(command)), /allow/);
  }
});

test("parsePayload rejects what is not a payload object", () => {
  assert.throws(() => parsePayload(""), /not a JSON hook payload/);
  assert.throws(() => parsePayload("[1]"), /payload object/);
  assert.deepEqual(parsePayload('{"tool_name":"Bash","tool_input":{"command":"ls"},"cwd":"/r"}'), {
    tool: "Bash",
    input: { command: "ls" },
    cwd: "/r",
  });
  assert.equal(edit("Read", { file_path: "/repo/.claude/skills/x" }), null);
});

// --- Spawned, as Claude Code runs it -------------------------------------------------------------

// The guard reads `docs/agents/guard.md` from the repository it sits in, so a spawned test runs a
// copy inside its own temporary repository: the host repository's rules never reach the result.
function runGuard(root, input, args = []) {
  return spawnSync(process.execPath, [path.join(root, "scripts", "guard.mjs"), ...args], { input, encoding: "utf8" });
}

function withRepo(fn) {
  const root = mkdtempSync(path.join(tmpdir(), "guard-"));
  try {
    execFileSync("git", ["init", "-q", "-b", "feat/x"], { cwd: root });
    mkdirSync(path.join(root, "scripts"));
    writeFileSync(path.join(root, "scripts", "sync-agents.mjs"), "");
    copyFileSync(guardPath, path.join(root, "scripts", "guard.mjs"));
    fn(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const hookOutput = (result) => (result.stdout ? JSON.parse(result.stdout).hookSpecificOutput : null);

test("spawned: bad stdin asks, and the exit code is 0", () => {
  withRepo((root) => {
    const result = runGuard(root, "not json");
    assert.equal(result.status, 0);
    const out = hookOutput(result);
    assert.equal(out.permissionDecision, "ask");
    assert.match(out.permissionDecisionReason, /guard failed/);
  });
});

test("spawned: a mirror edit and a force-push are denied, and a dirty reset asks, in a real repository", () => {
  withRepo((root) => {
    const payload = (tool, input) => JSON.stringify({ tool_name: tool, tool_input: input, cwd: root });
    const mirror = runGuard(root, payload("Edit", { file_path: path.join(root, ".claude", "skills", "x", "SKILL.md") }));
    assert.equal(mirror.status, 0);
    assert.equal(hookOutput(mirror).permissionDecision, "deny");
    const push = runGuard(root, payload("Bash", { command: "git push --force origin feat/x" }));
    assert.equal(hookOutput(push).permissionDecision, "deny");

    assert.equal(runGuard(root, payload("Bash", { command: "git reset --hard" })).stdout, "");
    writeFileSync(path.join(root, "a.txt"), "a\n");
    execFileSync("git", ["add", "a.txt"], { cwd: root, stdio: "ignore" });
    const reset = runGuard(root, payload("PowerShell", { command: "git reset --hard" }));
    assert.equal(hookOutput(reset).permissionDecision, "ask");
  });
});

test("spawned: --post warns about a NUL byte; a tool that does not match prints nothing", () => {
  withRepo((root) => {
    const file = path.join(root, "out.txt");
    writeFileSync(file, "a\u0000b");
    const post = runGuard(root, JSON.stringify({ tool_name: "Write", tool_input: { file_path: file }, cwd: root }), ["--post"]);
    assert.equal(post.status, 0);
    const out = hookOutput(post);
    assert.equal(out.hookEventName, "PostToolUse");
    assert.match(out.additionalContext, /NUL byte/);
    assert.equal(out.permissionDecision, undefined);
    writeFileSync(file, "clean\n");
    assert.equal(runGuard(root, JSON.stringify({ tool_name: "Write", tool_input: { file_path: file } }), ["--post"]).stdout, "");
    const read = runGuard(root, JSON.stringify({ tool_name: "Read", tool_input: { file_path: file }, cwd: root }));
    assert.deepEqual([read.status, read.stdout], [0, ""]);
  });
});

// Review finding 5: the spawned guard must apply its own repository's rules, not the host's.
test("spawned: the guard applies the guard.md of the repository it sits in", () => {
  withRepo((root) => {
    mkdirSync(path.join(root, "docs", "agents"), { recursive: true });
    writeFileSync(path.join(root, "docs", "agents", "guard.md"), "## Deny commands\n\n- `^echo marker-7f3a` — a rule only this temporary repository has.\n");
    const payload = JSON.stringify({ tool_name: "Bash", tool_input: { command: "echo marker-7f3a" }, cwd: root });
    const out = hookOutput(runGuard(root, payload));
    assert.equal(out?.permissionDecision, "deny");
    assert.match(out.permissionDecisionReason, /only this temporary repository/);
  });
});

// Review finding 4, end to end: a malformed guard.md warns at run time but fails --check.
test("spawned: a malformed guard.md leaves the universal rules working and fails --check", () => {
  withRepo((root) => {
    mkdirSync(path.join(root, "docs", "agents"), { recursive: true });
    writeFileSync(path.join(root, "docs", "agents", "guard.md"), "## Deny commands\n\nnot a bullet\n");
    const payload = (command) => JSON.stringify({ tool_name: "Bash", tool_input: { command }, cwd: root });
    const status = hookOutput(runGuard(root, payload("git status")));
    assert.equal(status.permissionDecision, undefined);
    assert.match(status.additionalContext, /\[guard G\] docs\/agents\/guard\.md:3:/);
    assert.equal(hookOutput(runGuard(root, payload("git push --force"))).permissionDecision, "deny");
    const check = runGuard(root, "", ["--check"]);
    assert.equal(check.status, 1);
    assert.match(check.stderr, /guard\.md:3:/);
  });
});

// --- Review findings, one test each --------------------------------------------------------------

test("finding 1: a heredoc inside $(...) is read as bash reads it, and message words are not commands", () => {
  const commit = "git commit -m \"$(cat <<'EOF'\nfix: don't push the branch\nEOF\n)\"";
  assert.equal(bash(commit, { git: DIRTY }), null);
  assert.equal(bash(commit.replace("don't push", "a) push"), { git: DIRTY }), null);
  assert.deepEqual(splitCommands(commit).map((c) => c.program), ["cat", "git"]);
  // Unparseable text asks only for a risky subcommand in command position.
  assert.equal(bash('git commit -m "fix: don\'t push the branch'), null);
  assert.equal(bash('echo "open: then git push the branch and reset --hard'), null);
  assert.deepEqual(rules(bash("echo hi; git push -f origin x 'open")), ["A5"]);
  assert.deepEqual(rules(bash("cd sub && git -C .. reset --hard \"open")), ["A5"]);
});

const PROTECTED_MD = "## Protected paths\n\n- `^\\.env(\\..*)?$` — local secrets.\n- `^private/$` — the companion.\n- `^docs/agents/x$` — kept by hand.\n";

test("finding 2: removing the root, a folder above it, or a folder holding a protected path asks", () => {
  const files = { "/repo/docs/agents/guard.md": PROTECTED_MD };
  for (const command of ["rm -rf .", "rm -rf *", "cd sub && rm -rf ..", "rm -rf /", "rm -rf docs", "rm -rf docs/agents", "rm -rf private"]) {
    assert.deepEqual([...new Set(rules(bash(command, { files })))], ["P"], command);
  }
  for (const command of ["rm -rf dist", "rm -rf docs/other", "rm -f .envrc.bak", "rm -rf node_modules/*"]) {
    assert.equal(bash(command, { files }), null, command);
  }
  assert.equal(bash("rm -rf .", {}), null);
});

test("finding 3: copies, writes, redirects and git rm onto a protected path ask", () => {
  const files = { "/repo/docs/agents/guard.md": PROTECTED_MD };
  const asks = [
    ["cp x .env", "Bash"],
    ["echo x > .env", "Bash"],
    ["echo x >> .env.local", "Bash"],
    ["cat a>.env", "Bash"],
    ["> .env", "Bash"],
    ["printf x | tee -a .env", "Bash"],
    ["git rm .env", "Bash"],
    ["git -C sub/.. rm -r private", "Bash"],
    ["Set-Content .env 'x'", "PowerShell"],
    ["'x' | Out-File -FilePath .env", "PowerShell"],
    ["Copy-Item a .env -Force", "PowerShell"],
    ["echo x > .env", "PowerShell"],
  ];
  for (const [command, tool] of asks) assert.deepEqual(rules(bash(command, { files, tool })), ["P"], command);
  const passes = [
    ["cp .env .env-copy-elsewhere/x", "Bash"],
    ["git rm --cached .env", "Bash"],
    ["git status > /dev/null 2>&1", "Bash"],
    ["git status > $null", "PowerShell"],
    ["cat .env", "Bash"],
  ];
  for (const [command, tool] of passes) assert.equal(bash(command, { files, tool }), null, command);
});

test("finding 4: guard.md takes hyphens, en dashes, * bullets, `none` and discover:; a malformed one only warns", () => {
  const settings = parseGuardSettings(
    [
      "## Deny commands",
      "",
      "- `a` - hyphen.",
      "* `b` – en dash.",
      "- `c` -- double hyphen.",
      "",
      "## Ask commands",
      "",
      "`none`",
      "",
      "## Protected paths",
      "",
      "discover: from workspace.md",
    ].join("\n"),
  );
  assert.deepEqual(settings.deny.map((rule) => [rule.pattern, rule.reason]), [["a", "hyphen."], ["b", "en dash."], ["c", "double hyphen."]]);
  assert.deepEqual([settings.ask, settings.protected], [[], []]);

  const files = { "/repo/docs/agents/guard.md": "## Deny commands\n\nnot a bullet\n" };
  const status = bash("git status", { files });
  assert.equal(status.decision, null);
  assert.match(status.context, /\[guard G\] docs\/agents\/guard\.md:3:.*universal rules apply/);
  const push = bash("git push --force", { files });
  assert.equal(push.decision, "deny");
  assert.match(push.context, /\[guard G\]/);
  assert.equal(edit("Edit", { file_path: "/repo/.claude/skills/a/SKILL.md" }, { files }).decision, "deny");
});

test("finding 6: popd and Pop-Location return to the directory pushd left", () => {
  const dirs = (text, shell = "bash") => resolveDirs(splitCommands(text, shell), "/repo", { platform: "linux" });
  assert.deepEqual(dirs("pushd ../clean && git status; popd; git reset --hard"), ["/clean", "/clean", "/repo", "/repo"]);
  assert.deepEqual(dirs("pushd a; pushd b; popd; git status"), ["/repo/a", "/repo/a/b", "/repo/a", "/repo/a"]);
  assert.deepEqual(dirs("popd; git status"), [null, null]);
  assert.deepEqual(dirs("Push-Location sub; Pop-Location; git status", "powershell"), ["/repo/sub", "/repo", "/repo"]);
  const ctx = fakeCtx(repo({ git: DIRTY }));
  const result = evaluate({ tool: "Bash", input: { command: "pushd ../clean && git status; popd; git reset --hard" }, cwd: "/repo" }, ctx);
  assert.deepEqual(rules(result), ["A4"]);
  assert.deepEqual(ctx.calls, ["/repo: git status --porcelain --untracked-files=normal"]);
});

test("finding 7: an unquoted < or > ends the word before it, so cat<<EOF is a heredoc", () => {
  assert.deepEqual(splitCommands("cat<<EOF > x.txt\ngit reset --hard\nEOF\n").map((c) => c.words.join(" ")), ["cat"]);
  assert.equal(bash("cat<<EOF > x.txt\ngit reset --hard\nEOF\n", { git: DIRTY }), null);
  assert.deepEqual(splitCommands("echo x>f 2>&1").map((c) => c.words.join(" ")), ["echo x"]);
});

test("finding 8: git checkout -, @ and @{-N} switch branches on a dirty tree", () => {
  for (const command of ["git checkout -", "git checkout @{-1}", "git checkout @"]) {
    assert.equal(bash(command, { git: DIRTY }), null, command);
  }
  assert.deepEqual(rules(bash("git checkout -- -", { git: DIRTY })), ["A4"]);
});

test("finding 9: the D3 reason names the lock entry's own source", () => {
  const lock = (source) => ({ "/repo/skills-lock.json": JSON.stringify({ version: 1, skills: { tdd: { source } } }) });
  const reason = (source) => edit("Edit", { file_path: "/repo/.agents/skills/tdd/SKILL.md" }, { files: lock(source) }).reason;
  assert.match(reason("mattpocock/skills"), /`mattpocock\/skills`/);
  assert.doesNotMatch(reason("mattpocock/skills"), /jwh3times/);
  assert.match(reason("."), /Edit `skills\/tdd\/` here and run `npm run build`/);
});

test("finding 10: cmd //c, Git Bash's spelling of cmd /c, is unwrapped", () => {
  for (const command of ["cmd //c git push -f origin x", "cmd.exe /C git push -f origin x", "cmd /s //C git push -f origin x"]) {
    assert.deepEqual(rules(bash(command)), ["D5"], command);
  }
});

// --- Linear time on adversarial input (CodeQL js/redos) ------------------------------------------

function timed(fn) {
  const started = performance.now();
  const value = fn();
  return { value, ms: performance.now() - started };
}

test("the unparseable-text check is linear on CodeQL's adversarial strings, and still decides right", () => {
  const shapes = [`git${" -C --".repeat(5000)}`, `git -${"-- -".repeat(5000)}`];
  for (const shape of shapes) {
    const plain = timed(() => bash(`${shape} 'open`));
    assert.ok(plain.ms < 200, `${shape.slice(0, 12)}...: ${plain.ms.toFixed(0)} ms`);
    assert.equal(plain.value, null);
    const risky = timed(() => bash(`${shape} push --force 'open`));
    assert.ok(risky.ms < 200, `${shape.slice(0, 12)}... push: ${risky.ms.toFixed(0)} ms`);
    assert.deepEqual(rules(risky.value), ["A5"]);
  }
  // The same subcommands are still found past prefixes, paths, -C and -c.
  for (const command of ["FOO=1 sudo /usr/bin/git -C x -c a=b reset --hard 'open", "gh -R o/r pr merge 1 'open", "git.exe push 'open"]) {
    assert.deepEqual(rules(bash(command)), ["A5"], command);
  }
  assert.equal(bash("git -C x status 'open"), null);
});

test("the other text-scanning paths stay fast on long adversarial input", () => {
  const n = 20000;
  const files = { "/repo/AGENTS.md": `${"<!-- shared:a -->\n".repeat(n)}text\n` };
  const cases = [
    [
      "W5 grep",
      () => {
        const raw = `grep -c ${"$'\\01".repeat(n)}`;
        const command = { program: "grep", words: ["grep", "-c"], raw, heredocs: [] };
        assert.deepEqual(windowsWarnings(command, { platform: "win32", tool: "Bash" }), []);
      },
    ],
    ["variables", () => splitCommands(`echo ${"$a".repeat(n)}`)],
    ["heredoc starts in $()", () => bash(`echo "$(cat ${"<<".repeat(n)}`)],
    ["guard.md heading", () => parseGuardSettings(`## ${" ".repeat(n)}#${" ".repeat(n)}x\n`)],
    ["guard.md bullet", () => assert.throws(() => parseGuardSettings(`## Deny commands\n\n- \`${"` ".repeat(n)}\n`))],
    ["AGENTS.md openers", () => edit("Write", { file_path: "/repo/AGENTS.md", content: "x" }, { files })],
  ];
  for (const [name, fn] of cases) {
    const { ms } = timed(fn);
    assert.ok(ms < 200, `${name}: ${ms.toFixed(0)} ms`);
  }
});

test("a shared block is found past an opener that never closes, and an opener inside a block is part of it", () => {
  const unclosed = ["<!-- shared:a -->", "stray", "<!-- shared:b -->", "body", "<!-- /shared:b -->", ""].join("\n");
  const files = { "/repo/AGENTS.md": unclosed };
  assert.deepEqual(rules(edit("Edit", { file_path: "/repo/AGENTS.md", old_string: "body", new_string: "x" }, { files })), ["D4"]);
  assert.deepEqual(rules(edit("Edit", { file_path: "/repo/AGENTS.md", old_string: "stray", new_string: "x" }, { files })), []);
});

test("a rethrown error keeps the error it replaces as its cause", () => {
  assert.throws(() => parsePayload("{"), (error) => error.cause instanceof SyntaxError);
  assert.throws(
    () => parseGuardSettings("## Deny commands\n\n- `(` — bad.\n"),
    (error) => /invalid regex/.test(error.message) && error.cause instanceof SyntaxError,
  );
});

test("classifyEdit needs no settings argument", () => {
  const ctx = fakeCtx(repo());
  assert.deepEqual(classifyEdit("Edit", { file_path: "/repo/src/a.js" }, { cwd: "/repo", ctx }), []);
});
