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
3. Read the evidence packet: `gate_reports/<package>/ai_context/<package>.ai-context.md`. If it
   does not exist, stop and tell the user to generate it first (the deterministic
   `ssis_validator ai-context --dtsx <path> --generated <dir> --combined` command, which writes
   there by default, or Claude Code's `/ssis-generate-evidence <package>` command) — do not
   attempt to reconstruct evidence any other way.

## Hard rules

- **The packet is the only input.** Never open the `.dtsx`, the generated project's other `.cs`
  files, or (regardless of how tempting) `gaps/`, `extract/`, `svk/`, `coverage-report.md`, or any
  generated project's own `README.md`. If asked to "double check against the real code," decline
  and explain why — suggest regenerating the packet instead if it's genuinely missing evidence.
- **Never fabricate a match.** Insufficient evidence in the packet is a real, valid outcome —
  report it as `AiInconclusive` (or, for a left-only gap, `AiConfirmedGap`) and state precisely
  what's missing, never guess.
- **Output format, per entry**, exactly:
  ```
  Component: <name> (<package>)
  Verdict: AiConfirmedMatch | AiSuspectedDiscrepancy | AiInconclusive | AiNotApplicable | AiFoundUnmappedImplementation | AiConfirmedGap
  Rationale: <one paragraph, citing specific quoted evidence from the packet>
  ```
  The first four verdicts are for `## Components` and `## Correlation gaps` entries.
  `AiFoundUnmappedImplementation`/`AiConfirmedGap` are ONLY for `## Left-only gaps` entries — a
  `LeftOnly` finding means Tier 1 actively confirmed no counterpart exists (stronger than a
  correlation gap's "couldn't correlate"), so the question is whether an unconfirmed candidate is
  genuinely an unmapped implementation under a different name/shape (`AiFoundUnmappedImplementation`)
  or the gap is real (`AiConfirmedGap`) — see `docs/ai-validation-prompt.md`'s "left-only gaps"
  section for the full two-part judgment.
- **Advisory only.** Never edit `gate_reports/<package>/gates/<package>.gate{1,2,3}.json`/`.md`
  (Tier 1's own deterministic output). Write your result to two files, next to the packet: the prose
  `gate_reports/<package>/ai_context/<package>.ai-review.md`, and a structured sidecar
  `gate_reports/<package>/ai_context/<package>.ai-review.json` — a flat JSON list of
  `{"component", "scope", "verdict", "rationale"}` objects, one per entry, so the deterministic
  `final-summary` CLI command can merge your verdicts without parsing prose. Tell the user a human
  still needs to read and confirm it — an `AiConfirmedMatch`/`AiFoundUnmappedImplementation` here
  is a recommendation, never a promotion to Tier 1's `Match`.

## Process

For every entry listed under the packet's `## Components`, `## Correlation gaps`, and
`## Left-only gaps` sections: read its Tier 1 finding, left-side evidence (or declared evidence),
right-side excerpt (or unconfirmed candidates), and any files it points to in the packet's
`## Sources` section (shared files are listed once there, referenced by name from each entry —
check there before concluding an entry has no usable evidence). Compare the two sides and write
the required verdict block, using the correct vocabulary for the section that entry came from. Do
this for every entry in one pass, then write both the combined `.ai-review.md` and the
`.ai-review.json` sidecar covering all of them.
