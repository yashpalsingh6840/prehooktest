---
name: ssis-tier2-adjudicate
description: Use when the user asks to review, adjudicate, or judge a Tier 2 AI evidence packet from the SSIS rewrite validator (an "ai-context" packet, an "ai-review", or a component's business-logic-equivalence question left Unverifiable by Gates 1-3) — e.g. "review the Tier 2 evidence for Package_Transforms", "adjudicate the LKP_Country packet", "is DER_Enrich actually implemented correctly". Not for running the deterministic gates themselves (validate/ai-context CLI commands) — only for the AI judgment step over an already-generated packet.
allowed-tools: Read, Write
---

# SSIS Tier 2 AI-assisted adjudication

Independent, advisory-only judgment over what Gates 1-3 (deterministic, rule-based) could not
resolve on their own — see `SSIS-Rewrite-Validation-Plan.md`'s two-tier architecture and
`docs/ai-validation-prompt.md` (the canonical rulebook this skill follows).

Your tools are restricted to `Read` and `Write` — no `Bash`, no `Glob`/`Grep`, no web access. This
is a deliberate, permission-layer guardrail: this skill's entire value depends on judging strictly
from the packet it's handed, not from whatever else it could find if it went looking.

## Before doing anything else

1. Identify the package name from the user's request (e.g. "Package_Transforms").
2. Read `docs/ai-validation-prompt.md` in full — it defines the required verdict vocabulary and
   the independence rule in detail. This skill's summary below is not a substitute for it.
3. Read the evidence packet: `ai_context/<package>/<package>.ai-context.md`. If it does not
   exist, stop and tell the user to generate it first (the deterministic
   `ssis_validator ai-context --dtsx <path> --generated <dir> --out ai_context/<package> --combined`
   command, or Claude Code's `/ssis-generate-evidence <package>` command) — do not attempt to
   reconstruct evidence any other way.

## Hard rules

- **The packet is the only input.** Never open the `.dtsx`, the generated project's other `.cs`
  files, or (regardless of how tempting) `gaps/`, `extract/`, `svk/`, `coverage-report.md`, or any
  generated project's own `README.md`. If asked to "double check against the real code," decline
  and explain why — suggest regenerating the packet instead if it's genuinely missing evidence.
- **Never fabricate a match.** Insufficient evidence in the packet is a real, valid outcome —
  report it as `AiInconclusive` and state precisely what's missing, never guess.
- **Output format, per component**, exactly:
  ```
  Component: <name> (<package>)
  Verdict: AiConfirmedMatch | AiSuspectedDiscrepancy | AiInconclusive | AiNotApplicable
  Rationale: <one paragraph, citing specific quoted evidence from the packet>
  ```
- **Advisory only.** Never edit `gate_reports/*.json`/`*.md` (Tier 1's own deterministic output).
  Write your result to a new file, `ai_context/<package>/<package>.ai-review.md`, and tell the
  user a human still needs to read and confirm it — an `AiConfirmedMatch` here is a
  recommendation, never a promotion to Tier 1's `Match`.

## Process

For every entry listed under the packet's `## Components` section: read its `Tier1 finding`,
`Left-side evidence`, `Right-side excerpt`, and any files it points to in the packet's `## Sources`
section (shared files are listed once there, referenced by name from each component — check there
before concluding a component has no usable evidence). Compare the two sides and write the
required verdict block. Do this for every component in one pass, then write the combined
`.ai-review.md` file covering all of them.
