---
description: Port TEST-ORACLE and LOCAL-DATA gaps into fills-library, apply them, and run dotnet test. The AI half of the generated-tests story. Claude-side equivalent of GitHub Copilot's /ssisx-test.
---

# ssisx: test-oracle + local-data gaps

Arguments given: `$ARGUMENTS`

Parse `$ARGUMENTS` for one value (ask the user directly if it's missing or ambiguous -- do not
guess):

- **Package** -- the package name, e.g. the folder name under `out/gaps/`.

This command is a thin pointer, not a copy: the full instructions -- checking for existing fills
first, the two gap kinds (`TEST-ORACLE`/`LOCAL-DATA`) and how to tell them apart, the known-good
testing API to rely on without re-reading `PackageHarness.cs`, the file conventions for each
kind, and the apply/build/test steps -- live in `.github/prompts/ssisx-test.prompt.md`, in this
same `Tools/` folder.

**What to do now:**

1. Read `.github/prompts/ssisx-test.prompt.md` in full.
2. Follow it exactly as written, substituting the package name above wherever it says
   `<package>` (or `${input:package:...}`, a Copilot-only placeholder).
3. This is the second of the two AI boundaries in the standard pipeline -- stop at Step 4 and
   report every non-mechanical judgement call for human review, per that file's own instruction.
   Never invent a connection string, seed data, or sample input beyond what a `LOCAL-DATA`
   packet explicitly asked for.

Do not restate or duplicate the prompt file's own content back to the user before starting --
just follow it and report progress as you go, per its own "Step 4"/"Logging" sections.
