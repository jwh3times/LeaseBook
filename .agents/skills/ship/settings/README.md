<!-- Generated from settings/README.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Repository settings

How a shared skill learns the facts of the repository it runs in. The skill body is the same in
every repository; what differs is written in that repository's settings files and read at run time.

## Where settings live

Each topic is one file, `docs/agents/<topic>.md`, in the repository. A setting is a `##` heading
carrying the setting's exact name from its definition, followed by its value. The definitions are
the `<topic>.md` files beside this one: each `##` heading with a **Read by** line is a setting; a
`## Procedure` section is a shared procedure that uses them. A settings file may hold other headings too — `issue-tracker.md` already
does — and those belong to someone else: leave them as they are.

A value is one of:

- **A literal** — a command in backticks, a path, a name, or a bulleted list.
- **`none`** — deliberately absent. The step that uses it is skipped, and the setting is never asked
  for again.
- **`discover: <how>`** — found at run time instead of written down, for a locator the repository
  keeps out of its public files. Follow the instruction each run.

A definition with a **Default** line has a value every repository shares unless it says otherwise.
Its heading is written only to override the default; a missing heading means the default. A
default may follow from another setting (`by Scheme`, `with the Private companion checked out`);
where it gives no value for this repository, the setting is completed like one without a default.

A **hook** is a `## Hook: <name>` heading with a numbered list of steps. The skill names its hooks
and the file each lives in, and runs the steps at that point. A missing hook means no extra steps;
hooks are never asked for.

## Step 0: read and complete the settings

1. Read `docs/agents/<topic>.md` for every topic the skill names.
2. Take each setting whose definition lists this skill under **Read by**, and find its heading. A
   present heading is the value; use it as written. A missing heading whose definition has a
   **Default** that gives this repository a value takes the default: it is never asked for.
3. If any others are missing (or a whole file is):
   - **When the user can answer** — scan the repository as each definition's **Discover** line
     says, then propose every missing value in one message, each with the evidence behind it.
     Apply the user's corrections and write the settings: a new file gets a `# <Topic>` title and
     the settings; an existing file gets the new headings appended, with every other line
     unchanged. Then continue the skill.
   - **When no user can answer** (running as a subagent, or in a non-interactive run) — write
     nothing. Stop, and report the missing settings to the caller.
4. Settings files are as public as the repository. A locator the repository keeps out of its
   public files is written as a `discover:` value; ask when unsure.
5. Name the settings files written in the skill's report. They are an ordinary uncommitted change,
   committed like any other.
