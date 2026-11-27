#!/usr/bin/env node
// A Claude Code hook that stops the tool calls this machine's rules forbid, in every session,
// subagent and cloud run, whether or not the rule was read. It is accident prevention, not a
// sandbox: it reads what a call says it will do, and a determined command gets past it.
//
//   node scripts/guard.mjs          # PreToolUse: payload on stdin, a decision on stdout
//   node scripts/guard.mjs --post   # PostToolUse: warns when a file just written holds a NUL byte
//   node scripts/guard.mjs --check  # validate docs/agents/guard.md; exit 1 when it is malformed
//
// A hit denies (the reason goes to the agent) or asks (the reason goes to the user); a Windows
// shell hazard only warns, as additional context. No finding prints nothing, and `allow` is never
// printed, so the user's own permission rules still apply. Any internal error asks with "guard
// failed: ..." and the script always exits 0: a broken guard must not lock every tool out.
//
// Universal rules are below; each repository adds its own in `docs/agents/guard.md` (the `guard`
// settings topic), and names there the wrappers that run Git, so the universal git rules reach
// them too. `scripts/sync-agents.mjs` registers this script in `.claude/settings.json`.
//
// This file is shared verbatim across repositories. Change it in jwh3times/skills, copy it to all.

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const defaultRoot = path.resolve(path.dirname(scriptPath), "..");

export const SETTINGS_FILE = "docs/agents/guard.md";
const EDIT_TOOLS = new Set(["Edit", "Write", "MultiEdit", "NotebookEdit"]);
const SHELL_TOOLS = new Map([
  ["Bash", "bash"],
  ["PowerShell", "powershell"],
]);
const MAIN_BRANCHES = new Set(["main", "master"]);
const GIT_TIMEOUT_MS = 3000;

export class ParseError extends Error {}

// --- Paths ---------------------------------------------------------------------------------------

const pathApi = (platform) => (platform === "win32" ? path.win32 : path.posix);
const slashes = (value) => value.replaceAll("\\", "/");
const fold = (value, platform) => (platform === "win32" ? value.toLowerCase() : value);

// An absolute path for `word` as written in a command run from `base`, or null when it cannot be
// known: a variable or substitution, `cd -`, or a Git Bash root path (`/tmp`) with no drive.
export function resolvePath(base, word, { platform = process.platform, home = null, dynamic = false } = {}) {
  if (dynamic || word === "-" || word === "") return null;
  let value = slashes(word);
  if (value === "~" || value.startsWith("~/")) {
    if (!home) return null;
    value = slashes(home) + value.slice(1);
  }
  const api = pathApi(platform);
  if (platform === "win32") {
    const drive = /^\/([a-zA-Z])(\/|$)/.exec(value);
    if (drive) value = `${drive[1].toUpperCase()}:/${value.slice(3)}`;
    else if (value.startsWith("/")) return null;
  }
  if (api.isAbsolute(value)) return slashes(api.resolve(value));
  if (base === null) return null;
  return slashes(api.resolve(base, value));
}

// `abs` relative to `root` with forward slashes, or null when it lies outside it.
function relativeInside(root, abs, platform) {
  const rel = slashes(pathApi(platform).relative(root, abs));
  if (rel === ".." || rel.startsWith("../") || pathApi(platform).isAbsolute(rel)) return null;
  return rel;
}

// The nearest folder at or above `start` holding `.git` (a folder, or a worktree's file).
export function repoRoot(start, ctx) {
  if (!start) return null;
  const api = pathApi(ctx.platform);
  let dir = slashes(start);
  for (;;) {
    if (ctx.exists(`${dir.replace(/\/$/, "")}/.git`)) return dir;
    const parent = slashes(api.dirname(dir));
    if (parent === dir) return null;
    dir = parent;
  }
}

// --- Payload -------------------------------------------------------------------------------------

export function parsePayload(text) {
  let data;
  try {
    data = JSON.parse(text);
  } catch (error) {
    throw new Error(`stdin is not a JSON hook payload (${error.message})`, { cause: error });
  }
  if (!data || typeof data !== "object" || Array.isArray(data)) {
    throw new Error("stdin is not a JSON hook payload object");
  }
  return {
    tool: typeof data.tool_name === "string" ? data.tool_name : "",
    input: data.tool_input && typeof data.tool_input === "object" ? data.tool_input : {},
    cwd: typeof data.cwd === "string" && data.cwd ? data.cwd : null,
  };
}

// --- Tokenizer -----------------------------------------------------------------------------------

// Reads the bodies of `heredocs`, which start at `i` (just past the newline of the line that
// opened them), into each one's `body` and `lines`. Returns the index just past the last delimiter.
function readHeredocBodies(text, i, heredocs) {
  for (const heredoc of heredocs) {
    const lines = [];
    for (;;) {
      if (i >= text.length) throw new ParseError(`heredoc without its closing ${heredoc.delimiter}`);
      let end = text.indexOf("\n", i);
      if (end < 0) end = text.length;
      const line = text.slice(i, end).replace(/\r$/, "");
      i = end + 1;
      if ((heredoc.strip ? line.replace(/^\t+/, "") : line) === heredoc.delimiter) break;
      lines.push(line);
    }
    heredoc.body = lines.join("\n");
    heredoc.lines = lines.length;
  }
  return i;
}

// Sticky (`y`) patterns run at an index of the text itself, never on a copy of its tail, so a long
// command costs linear time however many of them it holds.
const HEREDOC_START = /<<(-?)[ \t]*(?:'([^']*)'|"([^"]*)"|\\?([^\s;&|<>()'"]+))/y;
const FD_NUMBER = /\d+|-/y;
const VARIABLE = /\$(?:[\w:]+|[@*#?$!^-])/y;

function execAt(pattern, text, index) {
  pattern.lastIndex = index;
  return pattern.exec(text);
}

// The index of the `close` matching an `open` already consumed before `start`, skipping quoted
// text, comments and heredoc bodies, so `$(cat <<'EOF' ... EOF)` with an apostrophe or a `)` in
// the body closes where bash closes it. Bash escapes with a backslash; PowerShell with a
// backtick, where a backslash is a path.
function scanClose(text, start, open, close, shell) {
  const ps = shell === "powershell";
  const escapeChar = ps ? "`" : "\\";
  const heredocs = [];
  let depth = 1;
  for (let j = start; j < text.length; j += 1) {
    const c = text[j];
    if (c === escapeChar) j += 1;
    else if (!ps && c === "$" && text[j + 1] === "'") {
      for (j += 2; j < text.length && text[j] !== "'"; j += 1) if (text[j] === "\\") j += 1;
    } else if (c === "'") {
      j = text.indexOf("'", j + 1);
      if (j < 0) break;
    } else if (c === '"') {
      for (j += 1; j < text.length && text[j] !== '"'; j += 1) if (text[j] === escapeChar) j += 1;
    } else if (c === "#" && /[\s(;&|]/.test(text[j - 1] ?? " ")) {
      const end = text.indexOf("\n", j);
      j = (end < 0 ? text.length : end) - 1;
    } else if (!ps && c === "<" && text[j + 1] === "<" && text[j + 2] !== "<") {
      const heredoc = execAt(HEREDOC_START, text, j);
      if (heredoc) {
        heredocs.push({ delimiter: heredoc[2] ?? heredoc[3] ?? heredoc[4], strip: heredoc[1] === "-" });
        j += heredoc[0].length - 1;
      } else j += 1;
    } else if (c === "\n" && heredocs.length) {
      j = readHeredocBodies(text, j + 1, heredocs) - 1;
      heredocs.length = 0;
    } else if (c === open) depth += 1;
    else if (c === close && --depth === 0) return j;
  }
  throw new ParseError(`unclosed ${open}`);
}

// A fresh id for a subshell or nested script, which scopes the `cd` inside it.
function nextId(ids) {
  ids.n += 1;
  return ids.n;
}

// Splits a script into raw simple commands: words with quotes removed (a word built from `$x` or
// a substitution is `dynamic`), the text each came from, the subshell group it runs in, its
// heredocs and here-strings, and the substitutions inside it, still unparsed.
function lex(text, shell, group, ids) {
  const ps = shell === "powershell";
  const commands = [];
  const groups = [...group];
  const pending = [];
  let i = 0;
  let cmd = null;
  let word = null;
  let redirect = null;

  const begin = () => {
    cmd ??= { words: [], start: i, end: i, group: [...groups], heredocs: [], herestrings: [], nested: [], writes: [] };
  };
  const append = (s, dynamic = false) => {
    begin();
    word ??= { text: "", dynamic: false };
    word.text += s;
    if (dynamic) word.dynamic = true;
  };
  const endWord = () => {
    if (word === null) return;
    if (redirect === "herestring") cmd.herestrings.push(word.text);
    else if (redirect === "write") cmd.writes.push(word);
    else if (redirect?.heredoc) {
      const heredoc = { delimiter: word.text, strip: redirect.strip, body: "", lines: 0 };
      cmd.heredocs.push(heredoc);
      pending.push(heredoc);
    } else if (redirect === null) cmd.words.push(word);
    redirect = null;
    word = null;
  };
  const endCommand = () => {
    endWord();
    if (cmd) {
      cmd.end = i;
      commands.push(cmd);
    }
    cmd = null;
    redirect = null;
  };
  const nested = (from, to, raw) => {
    begin();
    cmd.nested.push(text.slice(from, to));
    append(raw, true);
  };
  const readHeredocs = () => {
    i = readHeredocBodies(text, i, pending);
    pending.length = 0;
  };
  const readRedirect = () => {
    begin();
    word = null; // a file-descriptor number before the operator
    if (!ps && text.startsWith("<<<", i)) {
      redirect = "herestring";
      return i + 3;
    }
    if (!ps && text.startsWith("<<", i)) {
      const strip = text[i + 2] === "-";
      redirect = { heredoc: true, strip };
      return i + (strip ? 3 : 2);
    }
    let j = i;
    if (text[j] === "&" || text[j] === "*") j += 1;
    j += 1;
    if (text[j] === ">" || text[j] === "|") j += 1;
    if (text[j] === "&") {
      const fd = execAt(FD_NUMBER, text, j + 1);
      if (fd) return j + 1 + fd[0].length;
      j += 1;
    }
    // An output redirect's target is a file the command writes, checked against Protected paths.
    redirect = text.slice(i, j).includes(">") ? "write" : "target";
    return j;
  };

  while (i < text.length) {
    const c = text[i];
    const next = text[i + 1];
    if (c === (ps ? "`" : "\\")) {
      if (next === "\n" || (next === "\r" && text[i + 2] === "\n")) i += next === "\n" ? 2 : 3;
      else {
        if (next !== undefined) append(next);
        i += 2;
      }
    } else if (c === "'") {
      append("");
      let j = i + 1;
      let s = "";
      for (;;) {
        const close = text.indexOf("'", j);
        if (close < 0) throw new ParseError("unterminated single quote");
        s += text.slice(j, close);
        if (ps && text[close + 1] === "'") {
          s += "'";
          j = close + 2;
        } else {
          i = close + 1;
          break;
        }
      }
      append(s);
    } else if (!ps && c === "$" && next === "'") {
      let j = i + 2;
      let s = "";
      while (j < text.length && text[j] !== "'") {
        s += text[j] === "\\" ? text.slice(j, j + 2) : text[j];
        j += text[j] === "\\" ? 2 : 1;
      }
      if (j >= text.length) throw new ParseError("unterminated $' quote");
      append(s);
      i = j + 1;
    } else if (c === '"') {
      append("");
      let j = i + 1;
      for (;;) {
        if (j >= text.length) throw new ParseError("unterminated double quote");
        const ch = text[j];
        if (ch === '"' && !(ps && text[j + 1] === '"')) break;
        if (ch === '"') {
          append('"');
          j += 2;
        } else if (ch === (ps ? "`" : "\\")) {
          const nx = text[j + 1] ?? "";
          if (ps || '$`"\\\n'.includes(nx)) append(nx === "\n" ? "" : nx);
          else append(`\\${nx}`);
          j += 2;
        } else if (ch === "$" && text[j + 1] === "(") {
          const close = scanClose(text, j + 2, "(", ")", shell);
          nested(j + 2, close, text.slice(j, close + 1));
          j = close + 1;
        } else if (!ps && ch === "`") {
          const close = text.indexOf("`", j + 1);
          if (close < 0) throw new ParseError("unterminated backtick");
          nested(j + 1, close, text.slice(j, close + 1));
          j = close + 1;
        } else {
          append(ch, ch === "$" && /[\w{@*#?$!:]/.test(text[j + 1] ?? ""));
          j += 1;
        }
      }
      i = j + 1;
    } else if (ps && c === "@" && (next === "'" || next === '"') && /^\r?\n/.test(text.slice(i + 2))) {
      const close = text.indexOf(`\n${next}@`, i + 2);
      if (close < 0) throw new ParseError("unterminated here-string");
      append(text.slice(text.indexOf("\n", i) + 1, close).replace(/\r$/, ""));
      i = close + 3;
    } else if ((c === "$" || (ps && c === "@")) && next === "(") {
      const close = scanClose(text, i + 2, "(", ")", shell);
      nested(i + 2, close, text.slice(i, close + 1));
      i = close + 1;
    } else if (!ps && c === "`") {
      const close = text.indexOf("`", i + 1);
      if (close < 0) throw new ParseError("unterminated backtick");
      nested(i + 1, close, text.slice(i, close + 1));
      i = close + 1;
    } else if (c === "$" && next === "{") {
      const close = scanClose(text, i + 2, "{", "}", shell);
      append(text.slice(i, close + 1), true);
      i = close + 1;
    } else if (c === "$" && /[\w@*#?$!:^-]/.test(next ?? "")) {
      const name = execAt(VARIABLE, text, i)[0];
      append(name, true);
      i += name.length;
    } else if (c === "#" && word === null) {
      const end = text.indexOf("\n", i);
      i = end < 0 ? text.length : end;
    } else if (ps && c === "<" && next === "#") {
      const end = text.indexOf("#>", i + 2);
      if (end < 0) throw new ParseError("unterminated <# comment");
      i = end + 2;
    } else if (c === " " || c === "\t" || c === "\r") {
      endWord();
      i += 1;
    } else if (c === "\n") {
      endCommand();
      i += 1;
      if (pending.length) readHeredocs();
    } else if (c === ";") {
      endCommand();
      i += 1;
    } else if (c === "&" && next === "&") {
      endCommand();
      i += 2;
    } else if (!ps && c === "&" && next === ">") {
      endWord();
      i = readRedirect();
    } else if (c === "&") {
      // PowerShell's call operator (`& 'C:/.../bash.exe' -lc '...'`) starts a command.
      if (ps && cmd === null) i += 1;
      else {
        endCommand();
        i += 1;
      }
    } else if (c === "|") {
      endCommand();
      i += next === "|" || (!ps && next === "&") ? 2 : 1;
    } else if (!ps && (c === "<" || c === ">") && next === "(") {
      const close = scanClose(text, i + 2, "(", ")", shell);
      nested(i + 2, close, text.slice(i, close + 1));
      i = close + 1;
    } else if (c === ">" || (!ps && c === "<")) {
      // An unquoted `<` or `>` is always an operator (`cat<<EOF`, `echo x>f`); a word of digits
      // (or PowerShell's `*`) right before it is the stream it redirects, not an argument.
      if (word !== null && !/^\d+$/.test(word.text) && !(ps && word.text === "*")) endWord();
      i = readRedirect();
    } else if (!ps && c === "(" && word?.text.endsWith("=")) {
      const close = scanClose(text, i + 1, "(", ")", shell);
      append(text.slice(i, close + 1));
      i = close + 1;
    } else if (c === "(" || c === ")") {
      endCommand();
      if (!ps && c === "(") groups.push(nextId(ids));
      else if (!ps && groups.length > group.length) groups.pop();
      i += 1;
    } else if ((c === "{" || c === "}") && word === null && (ps || next === undefined || /[\s;]/.test(next))) {
      endCommand();
      i += 1;
    } else {
      append(c);
      i += 1;
    }
  }
  endCommand();
  if (pending.length) throw new ParseError(`heredoc without its closing ${pending[0].delimiter}`);
  return commands;
}

// `git.exe`, `C:\Program Files\Git\bin\git.exe` and `/usr/bin/git` are all `git`.
export function programName(word) {
  return slashes(word)
    .split("/")
    .at(-1)
    .toLowerCase()
    .replace(/\.(exe|cmd|bat|com|ps1)$/, "");
}

const ASSIGNMENT = /^[A-Za-z_]\w*(\[[^\]]*\])?\+?=/;
const RESERVED = new Set(["if", "then", "else", "elif", "do", "while", "until", "!", "time"]);

// Drops what runs a command rather than being it: `VAR=x`, `env`, `sudo`, `command`, `exec`...
function stripPrefixes(words) {
  let i = 0;
  const flagsThen = (withValue) => {
    while (i < words.length && words[i].text.startsWith("-")) i += withValue.test(words[i].text) ? 2 : 1;
  };
  while (i < words.length) {
    const text = words[i].text;
    const program = programName(text);
    if (ASSIGNMENT.test(text) || RESERVED.has(text)) i += 1;
    else if (program === "env") {
      i += 1;
      while (i < words.length && (words[i].text.startsWith("-") || ASSIGNMENT.test(words[i].text))) {
        i += /^(-u|--unset|-C|--chdir|-S|--split-string)$/.test(words[i].text) ? 2 : 1;
      }
    } else if (program === "sudo" || program === "doas") {
      i += 1;
      flagsThen(/^-[ugpChrtUD]$/);
    } else if (["command", "builtin", "exec", "nohup", "nice"].includes(program)) {
      i += 1;
      flagsThen(/^-n$/);
    } else if (program === "timeout") {
      i += 1;
      flagsThen(/^(-s|-k|--signal|--kill-after)$/);
      i += 1;
    } else break;
  }
  return words.slice(i);
}

const SHELLS = new Set(["bash", "sh", "zsh", "dash", "ksh"]);

// The scripts a command hands to another shell, which are checked as commands too.
function wrappedScripts(program, args, raw) {
  if (SHELLS.has(program)) {
    let i = 0;
    let dashC = false;
    for (; i < args.length; i += 1) {
      const arg = args[i];
      if (arg === "--") {
        i += 1;
        break;
      }
      if (/^[-+][A-Za-z]+$/.test(arg)) {
        if (arg.startsWith("-") && arg.includes("c")) dashC = true;
        if (/o$/i.test(arg)) i += 1;
      } else if (arg === "--rcfile" || arg === "--init-file") i += 1;
      else if (!arg.startsWith("--")) break;
    }
    if (dashC) return i < args.length ? [{ text: args[i], shell: "bash" }] : [];
    // No script argument: the shell reads its standard input, so a heredoc body is commands.
    if (i >= args.length) {
      return [...raw.heredocs.map((h) => h.body), ...raw.herestrings].map((text) => ({ text, shell: "bash" }));
    }
    return [];
  }
  if (program === "eval") return [{ text: args.join(" "), shell: "bash" }];
  if (program === "invoke-expression" || program === "iex") return [{ text: args.join(" "), shell: "powershell" }];
  if (program === "cmd") {
    // Git Bash rewrites `/c` as a path, so `cmd //c` is how it is written there.
    const at = args.findIndex((arg) => /^\/{1,2}[ck]$/i.test(arg));
    return at < 0 ? [] : [{ text: args.slice(at + 1).join(" "), shell: "bash" }];
  }
  if (program === "pwsh" || program === "powershell") {
    for (let i = 0; i < args.length; i += 1) {
      const flag = args[i].toLowerCase().replace(/^\//, "-");
      if (/^-c(o(m(m(a(n(d)?)?)?)?)?)?$/.test(flag)) {
        return [{ text: args.slice(i + 1).join(" "), shell: "powershell" }];
      }
      if (flag === "-e" || flag === "-ec" || (flag.length >= 4 && "-encodedcommand".startsWith(flag))) {
        const decoded = Buffer.from(args[i + 1] ?? "", "base64").toString("utf16le");
        return [{ text: decoded, shell: "powershell" }];
      }
      if (/^-(f|file)$/.test(flag)) return [];
      if (/^-(ex|ep|executionpolicy|wd|workingdirectory|of|outputformat|if|inputformat|settingsfile|configurationname)$/.test(flag)) {
        i += 1;
      } else if (!flag.startsWith("-")) {
        // A bare argument is a command to Windows PowerShell and a script file to pwsh.
        return program === "powershell" ? [{ text: args.slice(i).join(" "), shell: "powershell" }] : [];
      }
    }
  }
  return [];
}

// Every simple command in `text`, in order, with the scripts it runs through substitutions,
// `bash -c`, `eval` or `pwsh -Command` alongside it. Throws a ParseError for text it cannot read
// (an unterminated quote or heredoc), which the caller scans as raw text instead.
export function splitCommands(text, shell = "bash", depth = 0, group = [], ids = { n: 0 }) {
  if (depth > 8) throw new ParseError("scripts nested too deeply");
  const out = [];
  for (const raw of lex(text, shell, group, ids)) {
    for (const inner of raw.nested) {
      out.push(...splitCommands(inner, shell, depth + 1, [...raw.group, nextId(ids)], ids));
    }
    const words = stripPrefixes(raw.words);
    if (words.length === 0 && raw.writes.length === 0) continue;
    const program = words.length ? programName(words[0].text) : "";
    out.push({
      shell,
      program,
      words: words.map((w) => w.text),
      dynamic: words.map((w) => w.dynamic),
      writes: raw.writes.map((w) => ({ text: w.text, dynamic: w.dynamic })),
      raw: text.slice(raw.start, raw.end).trim(),
      group: raw.group,
      heredocs: raw.heredocs,
    });
    for (const script of wrappedScripts(program, words.slice(1).map((w) => w.text), raw)) {
      out.push(...splitCommands(script.text, script.shell, depth + 1, [...raw.group, nextId(ids)], ids));
    }
  }
  return out;
}

// --- Directories ---------------------------------------------------------------------------------

const CD_PROGRAMS = new Set(["cd", "chdir", "set-location", "sl"]);
const PUSH_PROGRAMS = new Set(["pushd", "push-location"]);
const POP_PROGRAMS = new Set(["popd", "pop-location"]);
const GIT_GLOBAL_WITH_VALUE = new Set([
  "-C",
  "-c",
  "--git-dir",
  "--work-tree",
  "--namespace",
  "--super-prefix",
  "--config-env",
]);

// How many words the longest `Git wrappers` prefix takes at `words[at]` (so `cst git` beats
// `cst`), or 0 when none matches. The first word is compared as a program name.
function wrapperLength(words, at, wrappers) {
  let best = 0;
  for (const { words: prefix } of wrappers) {
    const matches = prefix.every((word, k) => (k === 0 ? programName(words[at] ?? "") === word : words[at + k] === word));
    if (matches && prefix.length > best) best = prefix.length;
  }
  return best;
}

// The index of git's first argument in a command's words: 1 after `git`, or past a `Git wrappers`
// prefix, which runs Git with the words after it. -1 when the command does not run Git.
export function gitStart(words, wrappers = []) {
  if (words.length === 0) return -1;
  if (programName(words[0]) === "git") return 1;
  return wrapperLength(words, 0, wrappers) || -1;
}

// The directory each command acts in: the session's, as moved by `cd`, `pushd` and `popd` (a
// subshell's move ends with it), and for git (or a Git wrapper) as moved again by `-C`. Null where
// it cannot be known.
export function resolveDirs(commands, cwd, { platform = process.platform, home = null, wrappers = [] } = {}) {
  const scopes = new Map();
  const start = cwd === null ? null : resolvePath(null, cwd, { platform });
  // Each scope has its directory and its pushd stack, both copied from the enclosing scope.
  const lookup = (group) => {
    const key = group.join(".");
    if (!scopes.has(key)) {
      const parent = group.length ? lookup(group.slice(0, -1)) : { dir: start, stack: [] };
      scopes.set(key, { dir: parent.dir, stack: [...parent.stack] });
    }
    return scopes.get(key);
  };
  const at = (base, command, i) =>
    resolvePath(base, command.words[i], { platform, home, dynamic: command.dynamic[i] });
  const target = (command) =>
    command.words.findIndex(
      (w, index) => index > 0 && w !== "--" && !/^-[LPe@]+$/.test(w) && !/^-(path|literalpath|lp)$/i.test(w),
    );
  return commands.map((command) => {
    const scope = lookup(command.group);
    const here = scope.dir;
    if (CD_PROGRAMS.has(command.program)) {
      const i = target(command);
      scope.dir = i < 0 ? (home ? resolvePath(null, home, { platform }) : null) : at(here, command, i);
      return scope.dir;
    }
    if (PUSH_PROGRAMS.has(command.program)) {
      const i = target(command);
      scope.stack.push(here);
      // A bare `pushd` swaps the top two entries, which is past what this tracks.
      scope.dir = i < 0 ? null : at(here, command, i);
      return scope.dir;
    }
    if (POP_PROGRAMS.has(command.program)) {
      scope.dir = scope.stack.length ? scope.stack.pop() : null;
      return scope.dir;
    }
    const start = gitStart(command.words, wrappers);
    if (start < 0) return here;
    let dir = here;
    for (let i = start; i < command.words.length && command.words[i].startsWith("-"); i += 1) {
      if (command.words[i] === "-C") dir = i + 1 < command.words.length ? at(dir, command, i + 1) : null;
      if (GIT_GLOBAL_WITH_VALUE.has(command.words[i])) i += 1;
    }
    return dir;
  });
}

// --- Rules ---------------------------------------------------------------------------------------

const finding = (level, rule, reason) => ({ level, rule, reason });
const shortFlags = (arg) => (/^-[A-Za-z]+$/.test(arg) ? arg.slice(1) : "");

// Asks when the work tree holds changes the command would destroy: tracked changes, or untracked
// files for `git clean`. A state it cannot read asks too (A5).
function dirtyGate(what, state, kind = "tracked") {
  const status = state.status?.() ?? null;
  if (status === null) {
    return [finding("ask", "A5", `Could not check ${state.dir ?? "the target directory"} for uncommitted changes before \`${what}\`.`)];
  }
  if (status[kind] === 0) return [];
  const count = `${status[kind]} ${kind === "tracked" ? "changed" : "untracked"} file(s)`;
  return [finding("ask", "A4", `\`${what}\` would discard uncommitted work: ${state.dir ?? "the work tree"} has ${count}.`)];
}

function classifyPush(args, state) {
  const findings = [];
  const positional = [];
  let force = false;
  let mirror = false;
  let remove = false;
  let all = false;
  let tags = false;
  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i];
    if (arg === "--") {
      positional.push(...args.slice(i + 1));
      break;
    }
    if (arg.startsWith("--")) {
      const name = arg.split("=")[0];
      if (name === "--force" || name === "--force-with-lease") force = true;
      else if (name === "--mirror") mirror = true;
      else if (name === "--delete") remove = true;
      else if (name === "--all" || name === "--branches") all = true;
      else if (name === "--tags") tags = true;
      else if (["--push-option", "--repo", "--receive-pack", "--exec"].includes(name) && !arg.includes("=")) i += 1;
    } else if (shortFlags(arg)) {
      const flags = shortFlags(arg);
      if (flags.includes("f")) force = true;
      if (flags.includes("d")) remove = true;
      if (flags.endsWith("o")) i += 1;
    } else positional.push(arg);
  }
  const refspecs = positional.slice(1);
  if (force || mirror || refspecs.some((ref) => ref.startsWith("+"))) {
    findings.push(
      finding(
        "deny",
        "D5",
        "Force-pushing (`--force`, `-f`, `--force-with-lease`, `--mirror` or a `+` refspec) rewrites the remote's history. Push without it; if history really has to be rewritten, the user runs that push.",
      ),
    );
  }
  if (remove || refspecs.some((ref) => ref.startsWith(":"))) {
    findings.push(finding("ask", "A2", "This push deletes a branch or tag on the remote."));
    return findings;
  }
  const targets = refspecs.map((ref) => {
    const spec = ref.replace(/^\+/, "");
    return spec.slice(spec.indexOf(":") + 1).replace(/^refs\/heads\//, "");
  });
  const onMain = "This push lands on `main` (or `master`) directly instead of through a pull request.";
  if (all || mirror || targets.some((target) => MAIN_BRANCHES.has(target))) {
    findings.push(finding("ask", "A1", onMain));
  } else if (targets.some((t) => t === "HEAD" || t === "@") || (refspecs.length === 0 && !tags)) {
    const branch = state.branch?.() ?? null;
    if (branch === null) {
      findings.push(finding("ask", "A5", `Could not tell which branch ${state.dir ?? "the target directory"} is on, so not whether this push lands on \`main\`.`));
    } else if (MAIN_BRANCHES.has(branch)) findings.push(finding("ask", "A1", onMain));
  }
  return findings;
}

// Findings for one git command; `args` are the words after `git`. `state` gives the directory
// and lazily reads its status and branch, so a harmless command costs no git call.
export function classifyGit(args, state = {}) {
  let i = 0;
  while (i < args.length && args[i].startsWith("-")) i += GIT_GLOBAL_WITH_VALUE.has(args[i]) ? 2 : 1;
  const sub = args[i];
  const rest = args.slice(i + 1);
  const has = (...names) => rest.some((arg) => names.includes(arg.split("=")[0]));
  const short = (letter) => rest.some((arg) => shortFlags(arg).includes(letter));

  if (sub === "push") return classifyPush(rest, state);
  if (sub === "branch") {
    const deleting = short("d") || has("--delete");
    if (short("D") || (deleting && (short("f") || has("--force")))) {
      return [finding("ask", "A3", "`git branch -D` deletes the branch even when its commits are merged nowhere.")];
    }
    return [];
  }
  if (sub === "reset") return has("--hard") ? dirtyGate("git reset --hard", state) : [];
  if (sub === "restore") {
    const staged = has("--staged") || short("S");
    const worktree = has("--worktree") || short("W");
    return !staged || worktree ? dirtyGate("git restore", state) : [];
  }
  if (sub === "switch") {
    return has("--discard-changes", "--force") || short("f") ? dirtyGate("git switch --discard-changes", state) : [];
  }
  if (sub === "clean") {
    if (has("--dry-run") || short("n")) return [];
    if (short("x") || short("X")) {
      return [finding("ask", "A4c", "`git clean -x` also deletes ignored files, such as `.env` and local settings, which `git status` does not show as changes.")];
    }
    return dirtyGate("git clean", state, "untracked");
  }
  if (sub === "checkout") {
    const positional = [];
    let paths = false;
    for (let j = 0; j < rest.length; j += 1) {
      const arg = rest[j];
      if (arg === "--") {
        paths = true;
        break;
      }
      if (arg.startsWith("--")) {
        const name = arg.split("=")[0];
        if (name === "--force" || name === "--pathspec-from-file") paths = true;
        if (name === "--orphan" && !arg.includes("=")) j += 1;
      } else if (shortFlags(arg)) {
        const flags = shortFlags(arg);
        if (flags.includes("f")) paths = true;
        if (/[bB]$/.test(flags)) j += 1;
      } else positional.push(arg);
    }
    // One word is a branch unless it names a path in the work tree; a second word is a path.
    if (!paths && positional.length >= 2) paths = true;
    if (!paths && positional.length === 1) {
      const [target] = positional;
      // `-`, `@` and `@{-N}` name the previous or current branch, never a path.
      const branchShorthand = target === "-" || target === "@" || /^@\{-\d+\}$/.test(target);
      paths = !branchShorthand && (target === "." || /[*?[]/.test(target) || (state.isPath?.(target) ?? true));
    }
    return paths ? dirtyGate("git checkout", state) : [];
  }
  return [];
}

const GH_VALUE_FLAGS = new Set(["-R", "--repo", "-b", "--body", "-F", "--body-file", "-t", "--subject", "-A", "--author-email", "--match-head-commit"]);

// Findings for one gh command; `args` are the words after `gh`.
export function classifyGh(args) {
  const positional = [];
  const flags = [];
  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i];
    if (arg.startsWith("-")) {
      flags.push(arg.split("=")[0]);
      if (GH_VALUE_FLAGS.has(arg) || (shortFlags(arg) && "RbFtA".includes(arg.at(-1)))) i += 1;
    } else positional.push(arg);
  }
  if (positional[0] !== "pr" || positional[1] !== "merge") return [];
  const named = [];
  for (const flag of flags) {
    if (["--admin", "--squash", "--rebase", "--auto"].includes(flag)) named.push(flag);
    const letters = shortFlags(flag);
    if (letters.includes("s")) named.push("--squash");
    if (letters.includes("r")) named.push("--rebase");
  }
  if (named.length === 0) return [];
  return [
    finding(
      "deny",
      "D6",
      `\`gh pr merge ${[...new Set(named)].join(" ")}\` is not how pull requests merge here: \`--admin\` skips the required checks, \`--auto\` merges later with no one watching, and \`--squash\`/\`--rebase\` replace the branch's commits. Merge through /land, on the user's word.`,
    ),
  ];
}

// Hazards of Claude Code's Bash tool on this Windows machine (Git Bash): each only warns.
export function windowsWarnings(command, { platform = process.platform, tool = "Bash" } = {}) {
  if (platform !== "win32" || tool !== "Bash") return [];
  const warnings = [];
  const warn = (rule, reason) => warnings.push(finding("warn", rule, reason));
  const { program, words, raw } = command;
  const bare = !/[\\/]/.test(words[0]);
  if (program === "node" && words.some((w) => /^(-e|--eval|-p|--print|-pe)$/.test(w)) && /[\\`]/.test(raw)) {
    warn("W1", "`node -e` with a backslash or backtick: the Bash tool eats backslashes and runs backticks as command substitution. Write the script to a file with the Write tool and run it by path.");
  }
  for (const heredoc of command.heredocs ?? []) {
    if (heredoc.lines > 100 || heredoc.body.includes("`")) {
      warn("W2", `A heredoc of ${heredoc.lines} line(s)${heredoc.body.includes("`") ? " holding backticks" : ""}: long ones, and ones with backticks or apostrophes, fail here with \`unexpected EOF\`. Write the content with the Write tool instead.`);
    }
  }
  if (program === "jq") warn("W3", "There is no `jq` on this machine. Use `gh --jq`, or a `.mjs` script run with node.");
  if (program === "gh" && words.some((w) => w === "--body" || w.startsWith("--body="))) {
    warn("W4", "`gh --body` with inline text: the Bash tool mangles backslashes and backticks in it. Write the body to a file and pass `--body-file`.");
  }
  // Each `$'...'` body is found with a split, then checked for a NUL escape with bounded counts.
  const ansiBodies = raw.split("$'").slice(1).map((rest) => rest.split("'")[0]);
  if (/^(grep|egrep|rg)$/.test(program) && ansiBodies.some((body) => /\\(?:x0{1,2}|0{1,3})(?![0-9])/.test(body))) {
    warn("W5", "`grep $'\\x00'` checks nothing here: bash makes the pattern empty, so it matches every line. Use `grep -cP '\\x00' <file>` (0 is clean).");
  }
  if (bare && (program === "python" || program === "python3")) {
    warn("W6", "A bare `python` may be the Microsoft Store stub, which hangs instead of failing. Prefer node.");
  }
  return warnings;
}

// --- Edits ---------------------------------------------------------------------------------------

// The `<!-- shared:X -->` ... `<!-- /shared:X -->` blocks in LF text, found line by line: each
// opener closes at the first matching closer after it, and an opener inside a closed block is
// part of that block, as with sync-agents.mjs. No regex runs over the whole text.
function sharedBlocks(text) {
  const blocks = [];
  const pending = new Map();
  let offset = 0;
  for (const line of text.split("\n")) {
    const end = offset + line.length;
    const opener = /^<!-- shared:([a-z-]+) -->$/.exec(line);
    const closer = /^<!-- \/shared:([a-z-]+) -->$/.exec(line);
    if (opener && !pending.has(opener[1])) pending.set(opener[1], offset);
    else if (closer && pending.has(closer[1])) {
      const start = pending.get(closer[1]);
      blocks.push({ name: closer[1], start, end, text: text.slice(start, end) });
      for (const [name, at] of pending) if (at >= start) pending.delete(name);
    }
    offset = end + 1;
  }
  return blocks;
}

function occurrences(text, needle) {
  const found = [];
  if (!needle) return found;
  for (let at = text.indexOf(needle); at >= 0; at = text.indexOf(needle, at + 1)) found.push(at);
  return found;
}

// Findings for an Edit, Write, MultiEdit or NotebookEdit call.
export function classifyEdit(tool, input, { cwd = null, ctx, settings = emptySettings() }) {
  const { platform } = ctx;
  const target = input.file_path ?? input.notebook_path;
  if (typeof target !== "string") return [];
  const file = resolvePath(cwd, target, { platform, home: ctx.home });
  if (file === null) return [];
  const findings = [];
  const deny = (rule, reason) => findings.push(finding("deny", rule, reason));

  const root = repoRoot(pathApi(platform).dirname(file), ctx);
  const rel = root === null ? null : relativeInside(root, file, platform);
  if (rel !== null) {
    const key = fold(rel, platform);
    const generated = ctx.exists(`${root}/scripts/sync-agents.mjs`);
    if (generated && key.startsWith(".claude/skills/")) {
      deny("D1", `\`${rel}\` is generated from \`.agents/skills/\` by scripts/sync-agents.mjs; an edit here is overwritten. Edit the source and run \`node scripts/sync-agents.mjs\`.`);
    }
    if (generated && key.startsWith(".codex/agents/")) {
      deny("D2", `\`${rel}\` is generated from \`.claude/agents/\` by scripts/sync-agents.mjs; an edit here is overwritten. Edit the source and run \`node scripts/sync-agents.mjs\`.`);
    }
    const installed = new RegExp("^\\.agents/skills/([^/]+)/", platform === "win32" ? "i" : "").exec(rel);
    if (installed) {
      const lockText = ctx.readFile(`${root}/skills-lock.json`);
      const locked = Object.entries(lockText === null ? {} : (JSON.parse(lockText).skills ?? {}));
      const entry = locked.find(([name]) => fold(name, platform) === fold(installed[1], platform));
      if (entry) {
        const [name, { source } = {}] = entry;
        // `.` is this repository installing its own skills (jwh3times/skills itself).
        const fix =
          source === "."
            ? `Edit \`skills/${name}/\` here and run \`npm run build\`.`
            : `Fix it at its source${typeof source === "string" ? `, \`${source}\`` : ""}, then re-run the install command.`;
        deny("D3", `\`.agents/skills/${name}/\` is installed from skills-lock.json, and reinstalling overwrites a local edit. ${fix}`);
      }
    }
    if (key === fold("AGENTS.md", platform)) {
      const current = ctx.readFile(file);
      const blocks = current === null ? [] : sharedBlocks(current.replaceAll("\r\n", "\n"));
      const blockReason = "The `<!-- shared:... -->` blocks in AGENTS.md are written by scripts/sync-agents.mjs from the `agents-md` settings topic. Change the text in jwh3times/skills (settings/agents-md.md) and re-run the sync; edit only outside the markers here.";
      if (blocks.length && tool === "Write" && typeof input.content === "string") {
        const after = sharedBlocks(input.content.replaceAll("\r\n", "\n")).map((b) => b.text);
        if (blocks.map((b) => b.text).join("\0") !== after.join("\0")) deny("D4", blockReason);
      } else if (blocks.length) {
        const text = current.replaceAll("\r\n", "\n");
        const edits = tool === "MultiEdit" && Array.isArray(input.edits) ? input.edits : [input];
        const overlaps = edits.some((edit) => {
          const needle = typeof edit.old_string === "string" ? edit.old_string.replaceAll("\r\n", "\n") : "";
          return occurrences(text, needle).some((at) =>
            blocks.some((block) => at < block.end && at + needle.length > block.start),
          );
        });
        if (overlaps) deny("D4", blockReason);
      }
    }
  }

  if (tool === "Write" && ctx.exists(file)) findings.push(...protectedFindings(file, "overwrites", settings, ctx));
  return findings;
}

// The literal text an anchored rule's matches all start with (`^docs/agents/x$` gives
// `docs/agents/x`), or null for an unanchored rule. A quantified last character is dropped.
export function literalPrefix(pattern) {
  if (!pattern.startsWith("^")) return null;
  let out = "";
  for (let i = 1; i < pattern.length; i += 1) {
    const c = pattern[i];
    if (c === "\\") {
      const escaped = pattern[i + 1] ?? "";
      if (!/^[^\w\s]$/.test(escaped)) return out;
      out += escaped;
      i += 1;
    } else if ("*?{".includes(c)) return out.slice(0, -1);
    else if (".+()[]|^$".includes(c)) return out;
    else out += c;
  }
  return out;
}

// Asks when `abs` is a protected path. A removal also asks when it takes a protected path with it:
// the repository root or a folder above it, whenever any path is protected, and a folder that an
// anchored rule's literal prefix lies inside (`rm -rf docs` for `^docs/agents/x$`).
function protectedFindings(abs, verb, settings, ctx, { removes = false } = {}) {
  if (settings.protected.length === 0 || abs === null) return [];
  const { platform, root } = ctx;
  const rel = relativeInside(root, abs, platform);
  if (rel === null || rel === "") {
    const holdsRoot = rel === "" || relativeInside(abs, root, platform) !== null;
    if (!removes || !holdsRoot) return [];
    const reasons = settings.protected.map((rule) => rule.reason).join(" ");
    return [finding("ask", "P", `This command ${verb} \`${abs}\`, which holds the whole repository and every protected path in ${SETTINGS_FILE}: ${reasons}`)];
  }
  const key = fold(rel, platform);
  const hit = (rule) => {
    const regex = platform === "win32" ? rule.folded : rule.regex;
    if (regex.test(rel) || regex.test(`${rel}/`)) return true;
    const prefix = removes ? literalPrefix(rule.pattern) : null;
    return Boolean(prefix) && fold(prefix, platform).startsWith(`${key}/`);
  };
  return settings.protected
    .filter(hit)
    .map((rule) => finding("ask", "P", `This command ${verb} \`${rel}\`, protected in ${SETTINGS_FILE}: ${rule.reason}`));
}

const REMOVERS = new Set(["rm", "rmdir", "unlink", "del", "erase", "rd", "remove-item", "ri", "mv", "move", "move-item", "mi"]);
const COPIERS = new Set(["cp", "copy", "copy-item", "cpi", "install"]);
const WRITERS = new Set(["tee", "truncate", "set-content", "sc", "add-content", "ac", "out-file", "new-item", "ni"]);
const PATH_PARAMETERS = /^-(path|literalpath|lp|filepath|destination)$/i;
const PS_SWITCHES = /^-(force|recurse|append|nonewline|passthru|whatif|confirm|noclobber|container)$/i;

// The words naming files a command deletes or overwrites, each marked `removes` when the path
// itself goes (a removal or a move's source) rather than gets written. Redirect targets count.
export function writtenPaths(command, wrappers = []) {
  const { program, shell } = command;
  const args = command.words.map((text, i) => ({ text, dynamic: command.dynamic[i] })).slice(1);
  const named = [];
  const positional = [];
  let target = null;
  for (let i = 0; i < args.length; i += 1) {
    const { text } = args[i];
    if (text === "-t" || text === "--target-directory") {
      target = args[i + 1] ?? null;
      i += 1;
    } else if (text.startsWith("--target-directory=")) target = { ...args[i], text: text.slice(19) };
    else if (PATH_PARAMETERS.test(text)) {
      if (args[i + 1]) named.push(args[i + 1]);
      i += 1;
    } else if (text.startsWith("-")) {
      // A PowerShell parameter that is not a switch takes the next word as its value.
      if (shell === "powershell" && !PS_SWITCHES.test(text) && args[i + 1] && !args[i + 1].text.startsWith("-")) i += 1;
    } else positional.push(args[i]);
  }
  const paths = command.writes.map((word) => ({ ...word, removes: false }));
  const add = (words, removes) => paths.push(...words.map((word) => ({ ...word, removes })));
  if (REMOVERS.has(program)) add([...named, ...positional], true);
  else if (COPIERS.has(program)) add(target ? [target] : named.length ? named : positional.slice(-1), false);
  else if (WRITERS.has(program)) add(named.length ? named : program === "tee" || program === "truncate" ? positional : positional.slice(0, 1), false);
  else if (gitStart(command.words, wrappers) > 0) {
    const git = args.slice(gitStart(command.words, wrappers) - 1);
    let k = 0;
    while (git[k]?.text.startsWith("-")) k += GIT_GLOBAL_WITH_VALUE.has(git[k].text) ? 2 : 1;
    if (git[k]?.text === "rm" && !git.some((word) => word.text === "--cached")) {
      add(git.slice(k + 1).filter((word) => !word.text.startsWith("-")), true);
    }
  }
  return paths;
}

// A glob stands for the folder it lists (`rm -rf *` removes the contents of `.`).
function globBase(word) {
  const at = word.search(/[*?[]/);
  if (at < 0) return word;
  const head = slashes(word.slice(0, at));
  const slash = head.lastIndexOf("/");
  return slash < 0 ? "." : head.slice(0, slash) || "/";
}

// --- Repository rules ----------------------------------------------------------------------------

const SECTIONS = new Map([
  ["Deny commands", "deny"],
  ["Ask commands", "ask"],
  ["Protected paths", "protected"],
  ["Git wrappers", "wrappers"],
]);

export function emptySettings() {
  return { deny: [], ask: [], protected: [], wrappers: [] };
}

// Parses `docs/agents/guard.md`. Each of its four headings holds `none` (or a `discover:` value,
// which sets no rules) or bullets of the form `` - `<regex>` — <reason> `` (`*` bullets, and an en
// dash or hyphen, work too), a reason continued on indented lines. Under `Git wrappers` the
// backticked text is a command prefix, its words matched literally (`cst git`). Other headings are
// someone else's and ignored. Anything else under the four throws, naming the line.
export function parseGuardSettings(text, file = SETTINGS_FILE) {
  const settings = emptySettings();
  const seen = new Set();
  let section = null;
  let state = null; // { line, none, count }
  let last = null;
  const close = () => {
    if (section && state.count === 0 && !state.none) {
      throw new Error(`${file}:${state.line}: '## ${section}' has no value; write \`none\` or bullets`);
    }
  };
  text.split(/\r?\n/).forEach((line, index) => {
    const n = index + 1;
    const heading = /^(#{1,6})[ \t]+(.*)$/.exec(line);
    if (heading) {
      close();
      // A closing run of `#` is not part of the title; trimmed by hand, not by an overlapping regex.
      let title = heading[2].trim();
      while (title.endsWith("#")) title = title.slice(0, -1);
      title = title.trim();
      section = heading[1] === "##" && SECTIONS.has(title) ? title : null;
      if (section && seen.has(section)) throw new Error(`${file}:${n}: '## ${section}' appears twice`);
      if (section) seen.add(section);
      state = { line: n, none: false, count: 0 };
      last = null;
      return;
    }
    if (!section) return;
    if (!line.trim()) {
      last = null;
      return;
    }
    // `none` (backticked or not) and a `discover:` value (settings/README.md) both mean no rules.
    if (/^`?none`?\.?$/i.test(line.trim()) || /^`?discover:/i.test(line.trim())) {
      if (state.count || state.none) throw new Error(`${file}:${n}: \`none\` must be the only value under '## ${section}'`);
      state.none = true;
      return;
    }
    const bullet = /^[-*][ \t]+`([^`]+)`[ \t]+(?:—|–|--?)[ \t]+(\S.*)$/.exec(line);
    if (bullet) {
      if (state.none) throw new Error(`${file}:${n}: '## ${section}' says \`none\` and also lists rules`);
      if (section === "Git wrappers") {
        const words = bullet[1].trim().split(/\s+/);
        last = { pattern: bullet[1], words: [programName(words[0]), ...words.slice(1)], reason: bullet[2].trim(), line: n };
        settings.wrappers.push(last);
        state.count += 1;
        return;
      }
      let regex;
      let folded;
      try {
        regex = new RegExp(bullet[1]);
        folded = new RegExp(bullet[1], "i");
      } catch (error) {
        throw new Error(`${file}:${n}: invalid regex \`${bullet[1]}\`: ${error.message}`, { cause: error });
      }
      last = { pattern: bullet[1], regex, folded, reason: bullet[2].trim(), line: n };
      settings[SECTIONS.get(section)].push(last);
      state.count += 1;
      return;
    }
    if (last && /^\s+\S/.test(line)) {
      last.reason += ` ${line.trim()}`;
      return;
    }
    const value = section === "Git wrappers" ? "<command prefix>" : "<regex>";
    throw new Error(
      `${file}:${n}: expected \`none\` or a bullet \`- \\\`${value}\\\` — <reason>\` under '## ${section}'; got ${JSON.stringify(line)}`,
    );
  });
  close();
  return settings;
}

// The repository's rules, and the problem with its file when it is malformed. A malformed file
// must not stop every call (a headless run would be locked out), so the universal rules apply
// alone and the problem rides along as a warning; `--check` fails on it.
function loadSettings(ctx) {
  const text = ctx.readFile(`${ctx.root}/${SETTINGS_FILE}`);
  if (text === null) return { settings: emptySettings(), warnings: [] };
  try {
    return { settings: parseGuardSettings(text), warnings: [] };
  } catch (error) {
    const reason = `${error.message}. Only the universal rules apply until it is fixed; \`node scripts/guard.mjs --check\` shows the problem.`;
    return { settings: emptySettings(), warnings: [finding("warn", "G", reason)] };
  }
}

// --- Decision ------------------------------------------------------------------------------------

// Deny beats ask; warnings ride along as context either way. Null when there is nothing to say.
export function decide(findings) {
  const lines = (level) => [
    ...new Set(findings.filter((f) => f.level === level).map((f) => `[guard ${f.rule}] ${f.reason}`)),
  ];
  const warnings = lines("warn");
  const context = warnings.length ? warnings.join("\n") : null;
  for (const decision of ["deny", "ask"]) {
    const reasons = lines(decision);
    if (reasons.length) return { decision, reason: reasons.join("\n"), context };
  }
  return context === null ? null : { decision: null, reason: null, context };
}

// The hook's stdout: empty, or one hookSpecificOutput object. Only `deny` and `ask` are ever
// written as a permission decision, and only for PreToolUse.
export function render(result, event = "PreToolUse") {
  if (!result) return "";
  const output = { hookEventName: event };
  if (event === "PreToolUse" && (result.decision === "deny" || result.decision === "ask")) {
    output.permissionDecision = result.decision;
    output.permissionDecisionReason = result.reason;
  }
  if (result.context) output.additionalContext = result.context;
  if (Object.keys(output).length === 1) return "";
  return JSON.stringify({ hookSpecificOutput: output });
}

function gitState(ctx) {
  const cache = new Map();
  const run = (dir, args) => {
    const key = `${dir}\0${args.join(" ")}`;
    if (!cache.has(key)) cache.set(key, dir === null ? null : ctx.git(dir, args));
    return cache.get(key);
  };
  return (dir) => ({
    dir,
    status: () => {
      const out = run(dir, ["status", "--porcelain", "--untracked-files=normal"]);
      if (out === null) return null;
      const lines = out.split("\n").filter(Boolean);
      const untracked = lines.filter((line) => line.startsWith("??")).length;
      return { tracked: lines.length - untracked, untracked };
    },
    branch: () => run(dir, ["rev-parse", "--abbrev-ref", "HEAD"])?.trim() || null,
    isPath: (word) => {
      const abs = dir === null ? null : resolvePath(dir, word, { platform: ctx.platform, home: ctx.home });
      return abs === null ? true : ctx.exists(abs);
    },
  });
}

function classifyShell(text, shell, tool, cwd, ctx, settings) {
  let commands;
  try {
    commands = splitCommands(text, shell);
  } catch (error) {
    if (!(error instanceof ParseError)) throw error;
    return unparsedFindings(text, error.message, settings);
  }
  const dirs = resolveDirs(commands, cwd, { platform: ctx.platform, home: ctx.home, wrappers: settings.wrappers });
  const state = gitState(ctx);
  const findings = [];
  commands.forEach((command, i) => {
    const args = command.words.slice(1);
    // A wrapper's command meets the shared git rules as git's; repository rules below still
    // match it as written.
    const start = gitStart(command.words, settings.wrappers);
    if (start > 0) findings.push(...classifyGit(command.words.slice(start), state(dirs[i])));
    if (command.program === "gh") findings.push(...classifyGh(args));
    if (command.words.length > 0) {
      const line = [command.program, ...args].join(" ");
      for (const rule of settings.deny) {
        if (rule.regex.test(line)) findings.push(finding("deny", "R", `${rule.reason} (${SETTINGS_FILE})`));
      }
      for (const rule of settings.ask) {
        if (rule.regex.test(line)) findings.push(finding("ask", "R", `${rule.reason} (${SETTINGS_FILE})`));
      }
      findings.push(...windowsWarnings(command, { platform: ctx.platform, tool }));
    }
    if (settings.protected.length) {
      for (const word of writtenPaths(command, settings.wrappers)) {
        const abs = resolvePath(dirs[i], globBase(word.text), { platform: ctx.platform, home: ctx.home, dynamic: word.dynamic });
        const verb = word.removes ? "removes or moves" : "writes";
        findings.push(...protectedFindings(abs, verb, settings, ctx, { removes: word.removes }));
      }
    }
  });
  return findings;
}

const STATEMENT_PREFIXES = new Set(["sudo", "env", "command", "exec", "nohup", "time"]);
const RISKY_GIT = /^(push|checkout|restore|reset|clean|branch|switch|rm)(\W|$)/;

// True when a statement (words joined by single spaces) starts with a risky git or gh subcommand,
// past the prefixes that only run it. Walked word by word, never by one regex over the statement,
// so an adversarial run of options costs linear time (CodeQL js/redos).
function startsRisky(statement, wrappers = []) {
  const words = statement.split(" ");
  let i = 0;
  while (i < words.length && (ASSIGNMENT.test(words[i]) || STATEMENT_PREFIXES.has(words[i]))) i += 1;
  const program = programName(words[i] ?? "");
  const wrapped = wrapperLength(words, i, wrappers);
  i += wrapped || 1;
  if (program === "git" || wrapped) {
    while (i < words.length && words[i].startsWith("-")) i += words[i] === "-C" || words[i] === "-c" ? 2 : 1;
    return RISKY_GIT.test(words[i] ?? "");
  }
  if (program === "gh") {
    if (words[i] === "-R") i += 2;
    return words[i] === "pr" && /^merge(\W|$)/.test(words[i + 1] ?? "");
  }
  return false;
}

// Text it cannot parse is never denied, only asked about. It is cut into rough statements, and
// one asks when it starts with a risky git or gh subcommand or matches a repository rule, so a
// word like `push` in a commit message is not mistaken for a command.
function unparsedFindings(text, why, settings) {
  const statements = text
    .split(/\r?\n|;|&&|\|\||\||&|\$\(|`|\(|\)|\{|\}/)
    .map((statement) => statement.trim().replace(/\s+/g, " "))
    .filter(Boolean);
  const findings = [];
  const risky = statements.find((statement) => startsRisky(statement, settings.wrappers));
  if (risky) {
    findings.push(finding("ask", "A5", `Could not parse this command (${why}), and it runs \`${risky.slice(0, 80)}\`.`));
  }
  for (const rule of [...settings.deny, ...settings.ask]) {
    if (statements.some((statement) => rule.regex.test(statement))) {
      findings.push(finding("ask", "A5", `Could not parse this command (${why}), and it matches a ${SETTINGS_FILE} rule: ${rule.reason}`));
    }
  }
  return findings;
}

// The decision for one hook payload. `ctx` carries every side effect: platform, home, the project
// root, `exists`, `readFile` (text or null) and `git(dir, args)` (stdout or null).
export function evaluate(payload, ctx, { post = false } = {}) {
  const { tool, input } = payload;
  const cwd = payload.cwd ?? ctx.cwd ?? null;
  if (post) {
    const target = input.file_path ?? input.notebook_path;
    const file = typeof target === "string" ? resolvePath(cwd, target, { platform: ctx.platform, home: ctx.home }) : null;
    const text = file === null ? null : ctx.readFile(file);
    const at = text === null ? -1 : text.indexOf("\u0000");
    if (at < 0) return null;
    return decide([
      finding("warn", "W7", `\`${target}\` now holds a NUL byte (character ${at}), usually a mangled escape. Find it with \`grep -cP '\\x00' <file>\` and fix it before going on.`),
    ]);
  }
  if (SHELL_TOOLS.has(tool)) {
    if (typeof input.command !== "string") return null;
    const { settings, warnings } = loadSettings(ctx);
    return decide([...warnings, ...classifyShell(input.command, SHELL_TOOLS.get(tool), tool, cwd, ctx, settings)]);
  }
  if (EDIT_TOOLS.has(tool)) {
    const { settings, warnings } = loadSettings(ctx);
    return decide([...warnings, ...classifyEdit(tool, input, { cwd, ctx, settings })]);
  }
  return null;
}

// --- Main ----------------------------------------------------------------------------------------

export function defaultContext() {
  return {
    platform: process.platform,
    home: os.homedir(),
    root: slashes(defaultRoot),
    cwd: slashes(process.cwd()),
    exists: (file) => existsSync(file),
    readFile: (file) => {
      try {
        return readFileSync(file, "utf8");
      } catch {
        return null;
      }
    },
    git: (dir, args) => {
      try {
        return execFileSync("git", args, {
          cwd: dir,
          encoding: "utf8",
          timeout: GIT_TIMEOUT_MS,
          stdio: ["ignore", "pipe", "ignore"],
          windowsHide: true,
          env: { ...process.env, GIT_OPTIONAL_LOCKS: "0" },
        });
      } catch {
        return null;
      }
    },
  };
}

function readStdin() {
  try {
    return readFileSync(0, "utf8");
  } catch {
    return "";
  }
}

function check() {
  const file = path.join(defaultRoot, SETTINGS_FILE);
  if (!existsSync(file)) {
    console.log(`No ${SETTINGS_FILE}: only the universal rules apply.`);
    return 0;
  }
  try {
    const settings = parseGuardSettings(readFileSync(file, "utf8"));
    console.log(
      `${SETTINGS_FILE}: ${settings.deny.length} deny, ${settings.ask.length} ask and ${settings.protected.length} protected-path rule(s), and ${settings.wrappers.length} Git wrapper(s).`,
    );
    return 0;
  } catch (error) {
    console.error(error.message);
    return 1;
  }
}

function main(argv) {
  if (argv.includes("--check")) return check();
  const post = argv.includes("--post");
  const event = post ? "PostToolUse" : "PreToolUse";
  let output;
  try {
    output = render(evaluate(parsePayload(readStdin()), defaultContext(), { post }), event);
  } catch (error) {
    const reason = `[guard] guard failed: ${error?.message ?? error}`;
    output = render(post ? { decision: null, context: reason } : { decision: "ask", reason }, event);
  }
  if (output) process.stdout.write(`${output}\n`);
  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch {
    process.exitCode = 0;
  }
}
