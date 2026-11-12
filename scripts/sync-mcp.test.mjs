import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import { readServers, renderCodexConfig, syncMcp } from "./sync-mcp.mjs";

const source = JSON.stringify({
  mcpServers: {
    zeta: { type: "stdio", command: "npx", args: ["-y", "@scope/pkg@1.2.3"] },
    alpha: { type: "http", url: "https://example.com/mcp" },
  },
});

function withRoot(fn) {
  const root = mkdtempSync(path.join(tmpdir(), "sync-mcp-"));
  try {
    writeFileSync(path.join(root, ".mcp.json"), source);
    return fn(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("renders http and stdio servers in name order", () => {
  const toml = renderCodexConfig(source);
  assert.match(toml, /^# GENERATED from \.mcp\.json/);
  const alpha = toml.indexOf("[mcp_servers.alpha]");
  const zeta = toml.indexOf("[mcp_servers.zeta]");
  assert.ok(alpha > 0 && zeta > alpha);
  assert.match(
    toml,
    /\[mcp_servers\.alpha\]\nurl = "https:\/\/example\.com\/mcp"\n/,
  );
  assert.match(
    toml,
    /\[mcp_servers\.zeta\]\ncommand = "npx"\nargs = \["-y", "@scope\/pkg@1\.2\.3"\]\nstartup_timeout_sec = 60\n$/,
  );
});

test("refuses an entry carrying env, headers or anything else", () => {
  for (const extra of [
    { env: { KEY: "x" } },
    { headers: { Authorization: "x" } },
    { cwd: "web" },
  ]) {
    const raw = JSON.stringify({
      mcpServers: { s: { type: "stdio", command: "npx", ...extra } },
    });
    assert.throws(
      () => readServers(raw),
      /shared servers carry no env, headers or other settings/,
    );
  }
});

test("refuses unsafe names, plain http, unknown types and bad JSON", () => {
  const cases = [
    [{ 'a"b': { type: "http", url: "https://x" } }, /server name/],
    [{ "a.b": { type: "http", url: "https://x" } }, /server name/],
    [{ s: { type: "http", url: "http://x" } }, /must be https/],
    [{ s: { type: "sse", url: "https://x" } }, /type must be/],
    [
      { s: { type: "stdio", command: "npx", args: ["a\nb"] } },
      /control character/,
    ],
  ];
  for (const [mcpServers, pattern] of cases) {
    assert.throws(() => readServers(JSON.stringify({ mcpServers })), pattern);
  }
  assert.throws(() => readServers("{"), /not valid JSON/);
  assert.throws(() => readServers("{}"), /needs an "mcpServers" object/);
});

test("--check fails on a missing or stale file and passes once synced", () => {
  withRoot((root) => {
    assert.equal(syncMcp(root, { check: true }), false);
    assert.equal(syncMcp(root), true);
    assert.equal(syncMcp(root, { check: true }), true);

    const target = path.join(root, ".codex/config.toml");
    writeFileSync(
      target,
      readFileSync(target, "utf8").replace("alpha", "beta"),
    );
    assert.equal(syncMcp(root, { check: true }), false);
  });
});

test("a CRLF checkout of the generated file still passes --check", () => {
  withRoot((root) => {
    syncMcp(root);
    const target = path.join(root, ".codex/config.toml");
    writeFileSync(target, readFileSync(target, "utf8").replace(/\n/g, "\r\n"));
    assert.equal(syncMcp(root, { check: true }), true);
  });
});
