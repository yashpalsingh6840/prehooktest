# Tier 2 AI-assisted adjudication — prompt & instructions

This document is the companion to `ssis_validator ai-context` (Phase C of the two-tier validation
architecture — see `SSIS-Rewrite-Validation-Plan.md`). It is **tool-agnostic**: paste it into
Claude chat, Copilot Chat, or any other AI chat tool, together with an `*.ai-context.md` (or
`.json`) packet produced by the CLI. It is not a Claude Code skill and requires no special
integration — that is deliberate, so a reviewer can run it wherever is convenient.

**Run the CLI with `--combined`** (`ssis_validator ai-context --dtsx <path> --generated <dir>
--out <dir> --combined`) unless you have a specific reason not to. It writes one
`<package>.ai-context.md` for the whole package instead of one file per component: every shared
generated source file is listed once in a `## Sources` section and every component references it
by path, and you paste the single file once and ask for every component's verdict back in one
reply — one AI roundtrip per package instead of one per component, with no duplicated source text
inflating the token count. The per-component instructions below apply identically; just work
through every entry under `## Components` in the same reply instead of one packet at a time.

## What Tier 1 already established (read this first)

Everything in an evidence packet has already been extracted and resolved by the deterministic
tool (Tier 1 — Gate 1/2/3). Tier 1 has already tried and failed to resolve the question
mechanically; that is *why* this packet exists. Do not re-derive anything Tier 1 already
resolved (the SSIS-side semantics, the resolved lineage, the generated method excerpt) — treat it
as ground truth about what each side *contains*. Your job is different and narrower: **judge
whether the generated C# method excerpt implements the same business logic as the SSIS-side
semantic evidence** — a reasoning task, not a structural comparison, which is exactly why a
deterministic tool cannot do it.

## The independence rule — this still applies to you

The packet you were given is the **only** input for this judgment. Do not:

- Open, browse, or ask about any other file in the generated project or its repository, even if
  your chat tool has file-browsing or code-search abilities available.
- Look inside `gaps/`, `extract/`, `svk/`, `coverage-report.md`, or any generated project's own
  `README.md` — these are the code-generation tool's own self-reporting about its own
  correctness, and judging against them would defeat the entire point of an independent
  validator (see `CLAUDE.md`'s independence rule).
- Assume, guess, or "fill in" what an unresolved field (e.g. an evidence field that is `null`, or
  a generated method excerpt that is `"(not found)"`) probably means. If the packet doesn't
  contain what you need to judge, say so — that is a valid, expected outcome (see
  `AiInconclusive` below), not a failure on your part.

## What to compare

1. **`evidence`** — the SSIS-side semantic evidence Tier 1 already resolved for this specific
   component (a FriendlyExpression, a Lookup's full shape including where its no-match output
   actually routes, a resolved aggregation/join source column, a Script Component/Task's real
   source file).
2. **`generated_method_excerpt`** — the brace-matched body of the one generated method Gate 2
   already correlated to this component's owning container. This excerpt is usually just the
   *owning Data Flow Task's* orchestration wiring (`var transform = new SomeTransform(); ...`),
   not the actual per-row logic.
3. **`related_generated_sources`** — the packet builder already resolved every identifier the
   excerpt references (and the component's own name) against this package's own generated files,
   so this is typically where the real logic actually lives: a sibling factory method in the same
   file, a `Mapping/<Name>.cs` transform class, or a `Fills/<Name>.Fills.cs` ported
   business-logic implementation. Always check this section before concluding a packet lacks
   enough evidence — an identifier that resolved to nothing here (absent from this dict) is an
   Etl.Core framework construct, not a missing lookup.
4. **`tier1_detail`** — why Tier 1 could not resolve this on its own; context, not evidence.

## Required output format

For **each** packet, produce:

```
Component: <component_name> (<package>)
Verdict: <one of the four verdicts below>
Rationale: <one paragraph, citing specific evidence from the packet — quote the exact
  FriendlyExpression/property/excerpt line(s) you compared, not a general impression>
```

### Verdict vocabulary (never a bare pass/fail — same discipline as Tier 1)

| Verdict | Meaning |
|---|---|
| `AiConfirmedMatch` | The generated logic, as far as the packet's excerpt shows, implements the same business logic as the SSIS-side evidence. |
| `AiSuspectedDiscrepancy` | You found a specific, citable difference between the two sides — state exactly what differs. |
| `AiInconclusive` | The packet does not contain enough evidence to judge either way (e.g. the excerpt doesn't reach the actual transform logic, or a field you needed is `null`/missing). Say precisely what additional evidence would be needed — do not guess past this. |
| `AiNotApplicable` | This packet's scope doesn't actually require a business-logic judgment (rare — flag it if you think Tier 1 mis-scoped a packet into Tier 2). |

## Human-in-the-loop — this is advisory, not a verdict

Your output does not get merged into `gate1`/`gate2`/`gate3`'s own reports, and it does not
become a new automatic pass/fail. Save it as `<package>.ai-review.md` alongside the evidence
packets. A human reads your cited evidence and rationale and makes the actual call — an
`AiConfirmedMatch` is a recommendation for a human to spot-check, never a promotion to Tier 1's
`Match`.
