---
description: The standard end-to-end ssisx pipeline, agent-driven -- extract, walkthrough, generate, fill (two AI boundaries), apply, build, test, coverage. Claude-side equivalent of GitHub Copilot's /ssisx-rewrite.
---

# ssisx: rewrite one package, a named few, or a whole folder -- end to end

Arguments given: `$ARGUMENTS`

Parse `$ARGUMENTS` as free text to fill in these three values (ask the user directly, in one
message, for anything that's missing or ambiguous -- do not guess):

- **Input folder** -- an absolute path to the SSIS folder/project to run against.
- **Package(s)** -- one or more package `ObjectName`s, comma-separated. If omitted entirely, this
  means "every package in the input folder" (see the Scope section this pulls in below).
- **Target framework** -- `net8.0` or `net10.0`, the CLIENT's actual runtime. If this is missing
  or looks like a placeholder, **stop and ask before generating anything** -- never default to
  `net10.0` silently (this mirrors `ssisx-generate.prompt.md`'s own rule).

This command is a thin pointer, not a copy: the full instructions -- the resume-detection logic,
the 9 steps, the two AI-boundary stopping rules, the closing-report/logging format -- live in
`.github/prompts/ssisx-rewrite.prompt.md`, in this same `Tools/` folder, which in turn
references `ssisx-extract.prompt.md`, `ssisx-walkthrough.prompt.md`, `ssisx-generate.prompt.md`,
`ssisx-fill.prompt.md`, and `ssisx-test.prompt.md` (all siblings in the same folder). Keeping
this file a pointer means it can never drift out of sync with the Copilot version -- there is
exactly one place this pipeline's real logic is written down.

**What to do now:**

1. Read `.github/prompts/ssisx-rewrite.prompt.md` in full.
2. Read whichever of its referenced sibling prompt files the step you're currently on actually
   needs (don't read all five up front if the resume-detection in Step 0 means you're skipping
   most of them -- read each one just before you follow it, same as you'd do naturally).
3. Follow it exactly as written, substituting the three values above wherever it says
   `<inputPath>`, `<package>`, and `<framework>` (or the `${input:...}` placeholders, which are a
   Copilot-only syntax -- for you, those three parsed values are the answer).
4. Everything in that file about being agent-driven applies to you directly: YOU take each step
   as your own tool call, stop at both named AI boundaries (Tier-1/2 gap fills, and
   test-oracle/local-data fills) and wait for the human's confirmation before continuing, even if
   a fill looks obviously correct.

Do not restate or duplicate the prompt file's own content back to the user before starting --
just follow it and report progress as you go, per its own "Closing report"/"Logging" sections.
