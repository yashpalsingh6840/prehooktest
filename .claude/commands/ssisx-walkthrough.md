---
description: Run svk walkthrough (+ sampledata) for one named package (or a named few) -- a human-readable execution-order review, deterministic, read-only against ssisx's own extracted spec. Claude-side equivalent of GitHub Copilot's /ssisx-walkthrough.
---

# svk: walkthrough (+ sampledata)

Arguments given: `$ARGUMENTS`

Parse `$ARGUMENTS` to fill in these two values (ask the user directly, in one message, for
anything missing or ambiguous -- do not guess):

- **Extracted spec folder** -- the `--out` folder from a prior `ssisx extract` run (e.g. `out`).
- **Package(s)** -- one or more package `ObjectName`s, comma-separated.

This command is a thin pointer, not a copy: the full instructions -- the bootstrap block, the
exact `svk walkthrough`/`svk sampledata` calls, and what to read afterward (and what NOT to
open) -- live in `.github/prompts/ssisx-walkthrough.prompt.md`, in this same `Tools/` folder.

**What to do now:**

1. Read `.github/prompts/ssisx-walkthrough.prompt.md` in full.
2. Follow it exactly as written, substituting the two values above wherever it says
   `<specPath>`/`<packages>` (or their `${input:...}` Copilot-only placeholder equivalents).
3. This is a review step -- generating C# is `/ssisx-generate`'s job, not this one, per that
   file's own closing instruction.

Do not restate or duplicate the prompt file's own content back to the user before starting --
just follow it and report progress as you go, per its own "Step 3"/"Logging" sections.
