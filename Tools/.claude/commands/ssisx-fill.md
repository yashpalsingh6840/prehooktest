---
description: Port Tier-2 script seams into fills-library, apply them, and build. Reads packets per component, not per gap. Claude-side equivalent of GitHub Copilot's /ssisx-fill.
---

# ssisx: fill Tier-2 gaps

Arguments given: `$ARGUMENTS`

Parse `$ARGUMENTS` for one value (ask the user directly if it's missing or ambiguous -- do not
guess):

- **Package** -- the package name, e.g. the folder name under `out/gaps/`.

This command is a thin pointer, not a copy: the full instructions -- checking for existing fills
first, reading work packets economically (one per component, not one per gap), the known-good
`Etl.Core` API to rely on without re-reading it, the exact file/provenance-comment convention,
and the apply/build steps -- live in `.github/prompts/ssisx-fill.prompt.md`, in this same
`Tools/` folder.

**What to do now:**

1. Read `.github/prompts/ssisx-fill.prompt.md` in full.
2. Follow it exactly as written, substituting the package name above wherever it says
   `<package>` (or `${input:package:...}`, a Copilot-only placeholder).
3. This is one of the two AI boundaries in the standard pipeline -- stop at Step 4 and report
   every non-mechanical port decision for human review before assuming this is done, per that
   file's own instruction. If a task's real logic cannot be reproduced against the documented
   abstractions, say so plainly rather than inventing a substitute.

Do not restate or duplicate the prompt file's own content back to the user before starting --
just follow it and report progress as you go, per its own "Step 4"/"Logging" sections.
