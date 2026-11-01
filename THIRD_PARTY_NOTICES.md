# Third-party notices

- **Audience:** Maintainers and anyone redistributing this repository
- **Status:** Living reference
- **Owner:** Maintainers
- **Last reviewed:** 2026-11-01

This repository's own license is in `LICENSE`. The files below are kept here under their own license.

## Agent skills

The skills in `.agents/skills/`, and the generated copies in `.claude/skills/`, come from two
sources, and `skills-lock.json` records which skill comes from where. Five of them, `end-session`,
`handoff`, `lets-go`, `ship` and `docs-updater`, come from
[jwh3times/skills](https://github.com/jwh3times/skills) (MIT). `handoff` derives in part from Matt
Pocock's handoff skill and carries his notice in its folder (`.agents/skills/handoff/LICENSE`). The
rest come from [mattpocock/skills](https://github.com/mattpocock/skills), under the license below.

```text
MIT License

Copyright (c) 2026 Matt Pocock

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
