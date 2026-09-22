---
description: Run ssisx generate for one named package (or a named few) and triage its gaps. Claude-side equivalent of GitHub Copilot's /ssisx-generate.
---

# ssisx: generate one package

Arguments given: `$ARGUMENTS`

Parse `$ARGUMENTS` to fill in these three values (ask the user directly, in one message, for
anything missing or ambiguous -- do not guess):

- **Input folder** -- an absolute path to the SSIS folder/project.
- **Package(s)** -- one or more package `ObjectName`s, comma-separated.
- **Target framework** -- `net8.0` or `net10.0`, the CLIENT's actual runtime. If this is missing
  or looks like a placeholder, **stop and ask before generating anything** -- never default to
  `net10.0` silently.

This command is a thin pointer, not a copy: the full instructions -- the bootstrap block, the
exact `generate` call with `--etl-core`/`--fills`/`--framework`, and how to triage
`generate-report.md`'s gaps by tier -- live in `.github/prompts/ssisx-generate.prompt.md`, in
this same `Tools/` folder.

**What to do now:**

1. Read `.github/prompts/ssisx-generate.prompt.md` in full.
2. Follow it exactly as written, substituting the three values above wherever it says
   `<inputPath>`/`<packages>`/`<framework>` (or their `${input:...}` Copilot-only placeholders).
3. Stop after triaging the gaps -- do not start writing fills unless asked, per that file's own
   closing instruction.

Do not restate or duplicate the prompt file's own content back to the user before starting --
just follow it and report progress as you go, per its own "Step 3"/"Logging" sections.
