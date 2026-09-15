---
description: Survey an SSIS folder with ssisx extract -- read-only, cheap. Claude-side equivalent of GitHub Copilot's /ssisx-extract.
---

# ssisx: extract (survey)

Arguments given: `$ARGUMENTS`

Parse `$ARGUMENTS` for one value (ask the user directly, in one message, if it's missing or
ambiguous -- do not guess):

- **Input folder** -- an absolute path to the SSIS folder, `.dtproj`, `.dtsx`, or `.ispac` to
  survey.

This command is a thin pointer, not a copy: the full instructions -- the bootstrap block that
resolves `ssisx`/`svk`/`dotnet`, the exact `extract` call, and what to read afterward (and what
NOT to open) -- live in `.github/prompts/ssisx-extract.prompt.md`, in this same `Tools/` folder.
(`extract` used to be six separate verbs -- `extract`/`graph`/`report`/`conformance`/`testgen`/
`diff` -- merged into one command in 2026-09; this pointer's own name is unaffected.)

**What to do now:**

1. Read `.github/prompts/ssisx-extract.prompt.md` in full.
2. Follow it exactly as written, substituting the input folder above wherever it says
   `<inputPath>` (or `${input:inputPath:...}`, a Copilot-only placeholder syntax -- for you, the
   parsed value above is the answer).
3. This is the cheap, read-only survey step -- do **not** run `generate` here, per that file's
   own instruction.

Do not restate or duplicate the prompt file's own content back to the user before starting --
just follow it and report progress as you go, per its own "Step 3"/"Logging" sections.
