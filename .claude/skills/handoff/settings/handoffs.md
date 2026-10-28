<!-- Generated from settings/handoffs.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: handoffs

`docs/agents/handoffs.md` — this repository's part of a handoff between machines. How the Handoffs
folder reaches a machine is that machine's config, not a repository setting (see
`.agents/skills/handoff/machine-config.md`). Each setting below is a `##` heading in that file.

## Map key

- **Read by:** handoff, lets-go
- **Value:** this repository's key in `handoff_map.json`, as a literal (`ApexRacers`, `skills`).
  Keys are the user's project names and match case- and punctuation-insensitively; the key also
  names the handoff documents (`apexracers-handoff-2026-10-26.md`).
- **Discover:** run
  `node .agents/skills/handoff/handoff-map.mjs get --key <origin repository name>`. `inMap: true` means the map already has the key, and `key` is its spelling there. Else
  look for a key the repository's old handoff skill or script used. Else propose the origin
  repository name.

## Extra document content

- **Read by:** handoff
- **Value:** `none`, or a bulleted list of what every handoff document from this repository also
  carries, beyond the standard sections: local state the other machine must recreate (files named,
  values never written), a standing caveat, a limit on what the document may quote.
- **Discover:** the repository's old handoff skill, for lines that apply to this repository only
  (local env files such as `.env.local` to recreate, "the private repository appears only as a
  commit hash"). Otherwise `none`.
