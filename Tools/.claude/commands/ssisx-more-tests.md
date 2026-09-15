---
description: Add more unit tests to an already-green, already-generatable package, reading only its README.md. NOT part of the gap-fill workflow. Claude-side equivalent of GitHub Copilot's /ssisx-more-tests.
---

# ssisx: add more unit tests to an already-green package

Arguments given: `$ARGUMENTS`

Parse `$ARGUMENTS` for one value (ask the user directly if it's missing or ambiguous -- do not
guess):

- **Package** -- the package name, e.g. the folder name under `out/generate/`.

This command is a thin pointer, not a copy: the full instructions -- confirming the package is
already green first, reading only the README's "Test coverage notes" section, picking a small
candidate set, the `MoreTests/` file convention, and the apply/build/test steps -- live in
`.github/prompts/ssisx-more-tests.prompt.md`, in this same `Tools/` folder.

**What to do now:**

1. Read `.github/prompts/ssisx-more-tests.prompt.md` in full.
2. Follow it exactly as written, substituting the package name above wherever it says
   `<package>` (or `${input:package:...}`, a Copilot-only placeholder).
3. This is **not** a gap-fill prompt -- if the package is not already green (builds clean, passes
   `dotnet test --filter Category!=Integration` with zero fills applied), stop and say so instead
   of running this; use `/ssisx-rewrite` or `/ssisx-generate` + `/ssisx-fill` first.
4. Stop at Step 4 and report what was written before applying, per that file's own instruction.

Do not restate or duplicate the prompt file's own content back to the user before starting --
just follow it and report progress as you go, per its own "Step 4"/"Logging" sections.
