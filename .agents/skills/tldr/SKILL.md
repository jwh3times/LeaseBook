---
name: tldr
description: A very brief catch-up on this session — what was done and why, what waits on the user, and the next step. Read-only. Use when the user says "tldr", "tl;dr", "catch me up" or "where are we".
argument-hint: "Optional: session, or a topic such as an issue or PR number"
---

# TL;DR

Catch the user up in about ten lines: what this session did and why, what waits on them, and the
next step. The conversation says what happened; a few live reads confirm what is still open, so
nothing is reported as waiting that has since merged or closed.

**Read-only.** The reads in step 2 are its only commands: it writes no file, posts nothing,
changes no label or field and commits nothing. It reads no settings file. It runs in the session
itself, where the conversation it summarizes is, never in a subagent. Run shell snippets in bash
(Git Bash on Windows).

## 1. Scope

- **No argument: the current stretch**, the piece of work the user's recent messages serve, traced
  back to the request that started it. A message that approves or continues it ("yes", "merge
  it", an answer to a question) extends the stretch; one that asks for something new starts the
  next. A stretch that ends in "merge it" reaches back through the PR to the work it carries.
- **`session`:** the whole session.
- **Anything else is a topic:** an issue or PR number, a skill, a word. The scope is the part of
  the session about it. When the session holds nothing on it, **Done** is the one line
  `Nothing on <topic> this session.`

The source is the conversation as it stands in context, a compaction summary included; transcript
files and logs stay unread. A line that rests on a compaction summary alone ends
`(from the session summary)`.

## 2. Check the live state

From the conversation, list the checkouts and branches the session worked on, every PR and issue
in the scope or waiting on the user, and every background task or agent the session started.
Confirm their state now, in five calls at most:

- **Each checkout:** `git status --short` for a dirty tree, and `git branch -vv` for every local
  branch's upstream, with the commits it is ahead or behind; a branch with no upstream was never
  pushed. A checkout other than this one takes `git -C <path>`.
- **PRs and issues:** one GraphQL call per repository, every number in one query:

  ```bash
  gh api graphql -f query='{ repository(owner: "<owner>", name: "<name>") {
    n130: issueOrPullRequest(number: 130) {
      ... on PullRequest { title state isDraft reviewDecision }
      ... on Issue { title state }
    }
  } }'
  ```

  A cloud session answers GraphQL with 403. There, read each number over REST instead, the ones
  waiting on the user first: `gh api repos/<owner>/<name>/issues/<n> --jq '[.title, .state,
  .pull_request.merged_at]'` answers for a PR too.
- **Background work:** the harness's task list, where it has one, for each task or agent the
  session started that has not reported back.

Each item is reported as it stands now, whoever changed it. An item left unread, by a read that
cannot run (`gh` missing or signed out, no network, no task list) or by the five-call limit, stays
as the session last saw it: mark its line `(as of <when the session saw it>)`, and name it on a
closing `Not checked:` line with why.

## 3. Report

```markdown
**Done**
- <outcome> — <why it was done>.
**You**
- <what the user has to do>
**Next:** <one step or command>
```

- **Done:** one line per outcome in the scope, each with its why: the reason for the work, not how
  it went. Anything that failed, was skipped or was left unverified gets its own line, stated as
  plainly as the rest: brevity governs wording, never bad news.
- **You:** everything still owed to the user from the whole session, whatever the scope: an
  approval the session is waiting for, a command only they can run (given exactly, one per line),
  a decision, a manual step. `Nothing.` when none is owed.
- **Next:** the step the session has pending, the next step of the work in progress or what
  follows the approval in **You**. With nothing pending: `Nothing pending: /what-next finds the
  next piece of work.`, named for the user to run.
- **About ten lines in all**, in plain words. A part with nothing to say is one line. Each issue or
  PR goes by its title and number (`Name every update in a Dependabot verdict (#130)`). File lists,
  test counts and review detail stay in the reports that already gave them.

Then stop and wait for the user.
