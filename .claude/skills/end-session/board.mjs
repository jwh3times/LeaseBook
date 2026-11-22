#!/usr/bin/env node
// Reads and writes one GitHub project board (Projects v2) for the shared skills: end-session files
// and updates items and audits the board against the issues. end-session writes to a board only
// through this script, so the quirks below are handled once.
//
//   node .agents/skills/end-session/board.mjs fields --board "<owner> <number>"
//   node .agents/skills/end-session/board.mjs get    --board "<owner> <number>" (--item <ref> | --item-id <id>)
//   node .agents/skills/end-session/board.mjs file   --board "<owner> <number>" --item <ref> [--set "<Field>=<value>" ...]
//   node .agents/skills/end-session/board.mjs set    --board "<owner> <number>" (--item <ref> | --item-id <id>) --set "<Field>=<value>" ...
//   node .agents/skills/end-session/board.mjs audit  --board "<owner> <number>" [--repo <owner/name> ...]
//                                                    [--status-field <name>] [--done <option>]
//
// <ref> is an issue or pull request, as `owner/name#123` or its github.com URL. `file` adds it to
// the board when it is not there yet, then sets the fields; `set` refuses an item not on the board.
// `--set "<Field>="` clears a field. `audit`'s status field and its done option default to `Status`
// and `Done`; name a board's own with `--status-field` and `--done`. The board is the `Board`
// setting's two words; this script reads no repository settings. Every command prints one JSON
// object, and exits 1 with `{ "error": ... }` on failure.
//
// What it handles, so no skill has to:
//   - A field is named as the board shows it, in any case: `item-list` reports custom fields
//     lowercased ("blocked by") and built-ins in camelCase, and a wrong accessor makes every write
//     look failed.
//   - A single-select value is an option's name, matched with case, spacing and the kind of dash
//     ignored, or a unique prefix of one (`P2` finds `P2 - Normal` on one board and `P2 — Soon` on
//     another). Option and field ids are looked up on every run, never stored. The audit's
//     `--done` is matched the same way, so it counts as done exactly what `--set` writes as done.
//   - An item is found from its issue or PR and read back by id, never from `item-list`, which
//     lags writes by up to a minute.
//   - All of an item's fields are written in one GraphQL mutation, and a secondary (burst) rate
//     limit or a 5xx is retried with a pause, so a run of edits does not trip GitHub's limiter.
//   - Every write is read back and compared; a value that did not take is an error.
//   - It never edits a field's options: `updateProjectV2Field` replaces the whole option list, and
//     clears the field on every item that used an option it drops.
//
// Writes need the gh token's `project` scope (`gh auth refresh -h github.com -s project`, in a real
// terminal); reads need `read:project`.

import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const COMMANDS = ["fields", "get", "file", "set", "audit"];
const WRITABLE = new Set(["SINGLE_SELECT", "TEXT", "NUMBER", "DATE"]);
const RETRY_PAUSES_MS = [2000, 5000, 10000];

export class BoardError extends Error {}

// ---------------------------------------------------------------------------------------------
// Arguments

export function parseArgs(argv) {
  const [command, ...rest] = argv;
  if (!COMMANDS.includes(command)) {
    throw new BoardError(`expected one of ${COMMANDS.join(", ")}; got ${command ?? "(none)"}`);
  }
  const options = {
    command,
    board: null,
    item: null,
    itemId: null,
    sets: [],
    repos: [],
    statusField: "Status",
    done: "Done",
  };
  const single = {
    "--board": "board",
    "--item": "item",
    "--item-id": "itemId",
    "--status-field": "statusField",
    "--done": "done",
  };
  for (let index = 0; index < rest.length; index += 1) {
    const flag = rest[index];
    const value = rest[index + 1];
    if (!(flag in single) && flag !== "--set" && flag !== "--repo") {
      throw new BoardError(`unknown argument: ${flag}`);
    }
    if (value === undefined) throw new BoardError(`${flag} requires a value`);
    index += 1;
    if (flag === "--set") options.sets.push(parseAssignment(value));
    else if (flag === "--repo") options.repos.push(parseRepo(value));
    else options[single[flag]] = value;
  }
  if (!options.board) throw new BoardError("--board is required");
  options.board = parseBoard(options.board);
  const needsItem = ["get", "file", "set"].includes(command);
  if (needsItem && !options.item && !options.itemId) {
    throw new BoardError(`${command} needs --item or --item-id`);
  }
  if (options.item && options.itemId) throw new BoardError("give --item or --item-id, not both");
  if (command === "file" && !options.item) {
    throw new BoardError("file needs --item: a board draft has no issue to add");
  }
  if (options.item) options.item = parseItemRef(options.item);
  if (command === "set" && options.sets.length === 0) throw new BoardError("set needs --set");
  if (!needsItem && (options.item || options.itemId || options.sets.length)) {
    throw new BoardError(`${command} takes no --item, --item-id or --set`);
  }
  if (command !== "audit" && options.repos.length) throw new BoardError("only audit takes --repo");
  return options;
}

export function parseBoard(value) {
  const parts = value.trim().split(/\s+/);
  const number = Number(parts[1]);
  if (parts.length !== 2 || !Number.isInteger(number) || number < 1) {
    throw new BoardError(`--board is "<owner> <number>", as the Board setting writes it; got "${value}"`);
  }
  return { owner: parts[0], number };
}

export function parseItemRef(value) {
  const short = /^([\w.-]+)\/([\w.-]+)#(\d+)$/.exec(value);
  const url = /^https:\/\/github\.com\/([\w.-]+)\/([\w.-]+)\/(?:issues|pull)\/(\d+)\/?$/.exec(value);
  const match = short ?? url;
  if (!match) throw new BoardError(`--item is owner/name#123 or an issue or PR URL; got "${value}"`);
  return { owner: match[1], name: match[2], number: Number(match[3]) };
}

function parseRepo(value) {
  const match = /^([\w.-]+)\/([\w.-]+)$/.exec(value);
  if (!match) throw new BoardError(`--repo is owner/name; got "${value}"`);
  return { owner: match[1], name: match[2] };
}

export function parseAssignment(value) {
  const at = value.indexOf("=");
  if (at <= 0) throw new BoardError(`--set is "<Field>=<value>"; got "${value}"`);
  return { field: value.slice(0, at).trim(), value: value.slice(at + 1).trim() };
}

// ---------------------------------------------------------------------------------------------
// Names

// Case, runs of spaces and the kind of dash do not distinguish two names on a board.
export function normalizeName(name) {
  return name
    .toLowerCase()
    .replace(/[‐-―−]/g, "-")
    .replace(/\s+/g, " ")
    .trim();
}

export function findField(fields, name) {
  const wanted = normalizeName(name);
  const field = fields.find((candidate) => normalizeName(candidate.name) === wanted);
  if (!field) {
    throw new BoardError(`no field "${name}" on the board; it has ${fields.map((f) => f.name).join(", ")}`);
  }
  return field;
}

export function findOption(field, value) {
  const wanted = normalizeName(value);
  const exact = field.options.filter((option) => normalizeName(option.name) === wanted);
  if (exact.length === 1) return exact[0];
  const prefixed = field.options.filter((option) => normalizeName(option.name).startsWith(wanted));
  if (prefixed.length === 1) return prefixed[0];
  const names = field.options.map((option) => option.name).join(", ");
  if (prefixed.length > 1) {
    throw new BoardError(`"${value}" matches more than one ${field.name} option: ${prefixed.map((o) => o.name).join(", ")}`);
  }
  throw new BoardError(`"${value}" is not a ${field.name} option; the options are ${names}`);
}

// The mutation value for one assignment, or null to clear the field.
export function fieldValue(field, raw) {
  if (!WRITABLE.has(field.dataType)) {
    throw new BoardError(`${field.name} is a ${field.dataType.toLowerCase()} field, which this script does not write`);
  }
  if (raw === "") return null;
  if (field.dataType === "SINGLE_SELECT") return { singleSelectOptionId: findOption(field, raw).id };
  if (field.dataType === "TEXT") return { text: raw };
  if (field.dataType === "NUMBER") {
    const number = Number(raw);
    if (!Number.isFinite(number)) throw new BoardError(`${field.name} takes a number; got "${raw}"`);
    return { number };
  }
  if (!/^\d{4}-\d{2}-\d{2}$/.test(raw)) throw new BoardError(`${field.name} takes a date, YYYY-MM-DD; got "${raw}"`);
  return { date: raw };
}

// ---------------------------------------------------------------------------------------------
// GraphQL

const FIELD_VALUES = `fieldValues(first: 50) { nodes {
  __typename
  ... on ProjectV2ItemFieldSingleSelectValue { name optionId field { ... on ProjectV2FieldCommon { name } } }
  ... on ProjectV2ItemFieldTextValue { text field { ... on ProjectV2FieldCommon { name } } }
  ... on ProjectV2ItemFieldNumberValue { number field { ... on ProjectV2FieldCommon { name } } }
  ... on ProjectV2ItemFieldDateValue { date field { ... on ProjectV2FieldCommon { name } } }
} }`;

// `kind` aliases `__typename`, since a dangling underscore fails some consumers' lint.
const CONTENT = `content {
  kind: __typename
  ... on Issue { number state url repository { nameWithOwner } }
  ... on PullRequest { number state url repository { nameWithOwner } }
  ... on DraftIssue { title }
}`;

const PROJECT_QUERY = `query Project($owner: String!, $number: Int!) {
  repositoryOwner(login: $owner) {
    ... on ProjectV2Owner {
      projectV2(number: $number) {
        id title number
        fields(first: 100) { nodes {
          ... on ProjectV2FieldCommon { id name dataType }
          ... on ProjectV2SingleSelectField { options { id name } }
        } }
      }
    }
  }
}`;

const CONTENT_QUERY = `query Content($owner: String!, $name: String!, $number: Int!) {
  repository(owner: $owner, name: $name) {
    issueOrPullRequest(number: $number) {
      __typename
      ... on Issue { id projectItems(first: 50, includeArchived: true) { nodes { id isArchived project { id } } } }
      ... on PullRequest { id projectItems(first: 50, includeArchived: true) { nodes { id isArchived project { id } } } }
    }
  }
}`;

const ITEM_QUERY = `query Item($id: ID!) {
  node(id: $id) {
    ... on ProjectV2Item { id isArchived project { id } ${CONTENT} ${FIELD_VALUES} }
  }
}`;

const ITEMS_QUERY = `query Items($id: ID!, $after: String) {
  node(id: $id) {
    ... on ProjectV2 {
      items(first: 100, after: $after) {
        pageInfo { hasNextPage endCursor }
        nodes { id isArchived ${CONTENT} ${FIELD_VALUES} }
      }
    }
  }
}`;

const OPEN_ISSUES_QUERY = `query OpenIssues($owner: String!, $name: String!, $after: String) {
  repository(owner: $owner, name: $name) {
    issues(states: OPEN, first: 100, after: $after) {
      pageInfo { hasNextPage endCursor }
      nodes { number title url projectItems(first: 50, includeArchived: true) { nodes { project { id } } } }
    }
  }
}`;

const ADD_MUTATION = `mutation Add($project: ID!, $content: ID!) {
  addProjectV2ItemById(input: { projectId: $project, contentId: $content }) { item { id } }
}`;

function isRetryable(message) {
  return /secondary rate limit|abuse detection|submitted too quickly|HTTP 50[234]|timed? ?out|ECONNRESET/i.test(
    message,
  );
}

function scopeHint(message) {
  return /required scopes|INSUFFICIENT_SCOPES|read:project|'project'/i.test(message)
    ? " The gh token needs the project scope: gh auth refresh -h github.com -s project (in a real terminal)."
    : "";
}

export function ghGraphql(query, variables) {
  const result = spawnSync("gh", ["api", "graphql", "--input", "-"], {
    input: JSON.stringify({ query, variables }),
    encoding: "utf8",
    windowsHide: true,
    maxBuffer: 64 * 1024 * 1024,
  });
  if (result.error) throw new BoardError(`could not run gh: ${result.error.message}`);
  let body = null;
  try {
    body = JSON.parse(result.stdout);
  } catch {
    // gh printed no JSON: report what it said.
  }
  if (result.status !== 0 || !body || body.errors) {
    const message = body?.errors?.map((error) => error.message).join("; ") || result.stderr.trim() || result.stdout.trim();
    const error = new BoardError(`${message}${scopeHint(message)}`);
    error.retryable = isRetryable(`${message} ${result.stderr}`);
    throw error;
  }
  return body.data;
}

function pause(ms) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

// Retries a call that failed on a rate limit or a server error, then gives up with its error.
export function withRetry(call, sleep = pause) {
  for (let attempt = 0; ; attempt += 1) {
    try {
      return call();
    } catch (error) {
      if (!error.retryable || attempt >= RETRY_PAUSES_MS.length) throw error;
      sleep(RETRY_PAUSES_MS[attempt]);
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Reading

export function readProject(api, board) {
  const data = api(PROJECT_QUERY, { owner: board.owner, number: board.number });
  const project = data.repositoryOwner?.projectV2;
  if (!project) throw new BoardError(`no project ${board.number} owned by ${board.owner}, or the token cannot read it`);
  const fields = project.fields.nodes
    .filter((field) => field?.id)
    .map((field) => ({ id: field.id, name: field.name, dataType: field.dataType, options: field.options ?? [] }));
  return { id: project.id, title: project.title, number: project.number, fields };
}

// The item's board fields by name, as the board shows them: an option's name, a text, a number,
// or a date. Built-in values (title, labels, repository) are left out.
export function itemFields(item) {
  const fields = {};
  for (const value of item.fieldValues?.nodes ?? []) {
    const name = value?.field?.name;
    if (!name || name === "Title") continue;
    if ("name" in value) fields[name] = value.name;
    else if ("text" in value) fields[name] = value.text;
    else if ("number" in value) fields[name] = value.number;
    else if ("date" in value) fields[name] = value.date;
  }
  return fields;
}

export function describeItem(item) {
  const content = item.content ?? {};
  const base = { id: item.id, archived: Boolean(item.isArchived), fields: itemFields(item) };
  if (content.kind === "DraftIssue") return { ...base, type: "draft", title: content.title };
  return {
    ...base,
    type: content.kind === "PullRequest" ? "pull request" : "issue",
    repository: content.repository?.nameWithOwner ?? null,
    number: content.number ?? null,
    state: content.state ?? null,
    url: content.url ?? null,
  };
}

function readItem(api, id) {
  const item = api(ITEM_QUERY, { id }).node;
  if (!item?.id) throw new BoardError(`no board item ${id}`);
  return item;
}

// The issue or PR's node id and its item on this board, if it has one.
function findContentItem(api, project, ref) {
  const data = api(CONTENT_QUERY, { owner: ref.owner, name: ref.name, number: ref.number });
  const content = data.repository?.issueOrPullRequest;
  if (!content) throw new BoardError(`no issue or pull request ${ref.owner}/${ref.name}#${ref.number}`);
  const item = content.projectItems.nodes.find((node) => node.project.id === project.id) ?? null;
  return { contentId: content.id, itemId: item?.id ?? null };
}

function resolveItem(api, project, options) {
  if (options.itemId) {
    const item = readItem(api, options.itemId);
    if (item.project?.id !== project.id) throw new BoardError(`item ${options.itemId} is not on this board`);
    return { itemId: item.id, contentId: null };
  }
  return findContentItem(api, project, options.item);
}

// ---------------------------------------------------------------------------------------------
// Writing

export function buildFieldMutation(projectId, itemId, writes) {
  const lines = writes.map((write, index) => {
    const input = `projectId: ${JSON.stringify(projectId)}, itemId: ${JSON.stringify(itemId)}, fieldId: ${JSON.stringify(write.field.id)}`;
    if (write.value === null) {
      return `f${index}: clearProjectV2ItemFieldValue(input: { ${input} }) { projectV2Item { id } }`;
    }
    const [key, value] = Object.entries(write.value)[0];
    return `f${index}: updateProjectV2ItemFieldValue(input: { ${input}, value: { ${key}: ${JSON.stringify(value)} } }) { projectV2Item { id } }`;
  });
  return `mutation Fields {\n  ${lines.join("\n  ")}\n}`;
}

function expectedValue(write) {
  if (write.value === null) return undefined;
  if (write.option) return write.option.name;
  return Object.values(write.value)[0];
}

function applyFields(api, project, itemId, assignments) {
  const writes = assignments.map((assignment) => {
    const field = findField(project.fields, assignment.field);
    const value = fieldValue(field, assignment.value);
    const option = value?.singleSelectOptionId
      ? field.options.find((candidate) => candidate.id === value.singleSelectOptionId)
      : null;
    return { field, value, option };
  });
  const before = itemFields(readItem(api, itemId));
  const pending = writes.filter((write) => before[write.field.name] !== expectedValue(write));
  if (pending.length) api(buildFieldMutation(project.id, itemId, pending), {});
  const after = pending.length ? itemFields(readItem(api, itemId)) : before;
  const missed = writes.filter((write) => after[write.field.name] !== expectedValue(write));
  if (missed.length) {
    throw new BoardError(
      `the board did not take ${missed.map((write) => `${write.field.name}=${expectedValue(write) ?? "(clear)"}`).join(", ")}; it reads ${JSON.stringify(after)}`,
    );
  }
  const changed = pending.map((write) => ({
    field: write.field.name,
    from: before[write.field.name] ?? null,
    to: after[write.field.name] ?? null,
  }));
  const unchanged = writes.filter((write) => !pending.includes(write)).map((write) => write.field.name);
  return { changed, unchanged };
}

// ---------------------------------------------------------------------------------------------
// Commands

function commandFields(api, options) {
  const project = readProject(api, options.board);
  return {
    board: { title: project.title, owner: options.board.owner, number: project.number },
    fields: project.fields.map((field) => ({
      name: field.name,
      type: field.dataType.toLowerCase(),
      writable: WRITABLE.has(field.dataType),
      ...(field.dataType === "SINGLE_SELECT" ? { options: field.options.map((option) => option.name) } : {}),
    })),
  };
}

function commandGet(api, options) {
  const project = readProject(api, options.board);
  const { itemId } = resolveItem(api, project, options);
  return { item: itemId ? describeItem(readItem(api, itemId)) : null };
}

function commandWrite(api, options) {
  const project = readProject(api, options.board);
  const resolved = resolveItem(api, project, options);
  let itemId = resolved.itemId;
  let added = false;
  if (!itemId) {
    if (options.command !== "file") {
      const ref = options.item;
      throw new BoardError(`${ref.owner}/${ref.name}#${ref.number} is not on the board; file adds it`);
    }
    itemId = api(ADD_MUTATION, { project: project.id, content: resolved.contentId }).addProjectV2ItemById.item.id;
    added = true;
  }
  const result = options.sets.length ? applyFields(api, project, itemId, options.sets) : { changed: [], unchanged: [] };
  return { item: describeItem(readItem(api, itemId)), added, ...result };
}

function pages(api, query, variables, select) {
  const nodes = [];
  let after = null;
  do {
    const connection = select(api(query, { ...variables, after }));
    nodes.push(...connection.nodes);
    after = connection.pageInfo.hasNextPage ? connection.pageInfo.endCursor : null;
  } while (after);
  return nodes;
}

function commandAudit(api, options) {
  const project = readProject(api, options.board);
  const statusField = findField(project.fields, options.statusField);
  if (statusField.dataType !== "SINGLE_SELECT") {
    throw new BoardError(
      `${statusField.name} is a ${statusField.dataType.toLowerCase()} field; --status-field names a single-select field`,
    );
  }
  // Resolved as a write resolves it, so `--done Done` counts what `--set "Status=Done"` set, and a
  // done option the field does not have is an error rather than every closed item reported.
  const done = findOption(statusField, options.done);
  const items = pages(api, ITEMS_QUERY, { id: project.id }, (data) => data.node.items)
    .filter((item) => !item.isArchived)
    .map(describeItem);
  const statusOf = (item) => item.fields[statusField.name] ?? null;
  const isDone = (item) => statusOf(item) === done.name;
  const summary = (item) => ({
    id: item.id,
    type: item.type,
    ...(item.type === "draft" ? { title: item.title } : { repository: item.repository, number: item.number, url: item.url }),
    status: statusOf(item),
  });
  const closedNotDone = items
    .filter((item) => item.type !== "draft" && item.state !== "OPEN" && !isDone(item))
    .map(summary);
  const openDone = items.filter((item) => item.type !== "draft" && item.state === "OPEN" && isDone(item)).map(summary);
  const noStatus = items.filter((item) => statusOf(item) === null).map(summary);
  const missing = [];
  for (const repo of options.repos) {
    const issues = pages(api, OPEN_ISSUES_QUERY, { owner: repo.owner, name: repo.name }, (data) => data.repository.issues);
    for (const issue of issues) {
      if (!issue.projectItems.nodes.some((node) => node.project.id === project.id)) {
        missing.push({ repository: `${repo.owner}/${repo.name}`, number: issue.number, title: issue.title, url: issue.url });
      }
    }
  }
  return {
    board: { title: project.title, owner: options.board.owner, number: project.number },
    status: { field: statusField.name, done: done.name },
    items: items.length,
    closedNotDone,
    openDone,
    noStatus,
    ...(options.repos.length ? { missing } : {}),
  };
}

const HANDLERS = { fields: commandFields, get: commandGet, file: commandWrite, set: commandWrite, audit: commandAudit };

export function run(argv, { graphql = ghGraphql, sleep = pause } = {}) {
  const options = parseArgs(argv);
  const api = (query, variables) => withRetry(() => graphql(query, variables), sleep);
  return HANDLERS[options.command](api, options);
}

function main() {
  try {
    console.log(JSON.stringify(run(process.argv.slice(2)), null, 2));
  } catch (error) {
    console.log(JSON.stringify({ error: error.message }, null, 2));
    process.exitCode = 1;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
