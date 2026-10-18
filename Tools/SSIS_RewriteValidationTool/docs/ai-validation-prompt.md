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
- Look inside a `gaps/`, `extract/`, `svk/`, `coverage-report.md`, or `README.md` that belongs to
  the **generated project** (i.e. under the `--generated` folder passed to the CLI) — these are
  the code-generation tool's own self-reporting about its own correctness, and judging against
  them would defeat the entire point of an independent validator (see `CLAUDE.md`'s independence
  rule). This is unrelated to this validator's own `gate_reports/<package>/gaps/` folder (this
  repo's standalone correlation-gap/left-only-gap packets, produced by `ai-context`'s non-combined
  mode) — reading one of those packets is exactly what this document is about.
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

## A second, different question: correlation gaps

Some packets are not "does this code implement the same logic" but "we don't even know which
generated code represents this, if any." These show up in a `## Correlation gaps` section (or a
standalone `*.correlation-gap.ai-context.md` file) instead of `## Components`, and carry a
different shape:

- **`declared_columns`** — the SSIS side's real declared column shape. Not in question.
- **`known_correlate`**, when present — Gate 1 already independently matched this component to a
  specific generated construct by SQL content (not name). This is real, established evidence, not
  a guess — but you should still confirm it's a sensible match before relying on it.
- **`unconfirmed_candidates`**, when `known_correlate` is absent — a small, capped list of places
  in the generated project whose name merely mentions this component (found by a bounded text
  search, not an open-ended browse). None of these are confirmed; you decide whether any is
  plausible.

Your job on one of these has **two parts**, in order: (1) decide whether the known correlate or
one of the candidates is genuinely the generated code for this component — cite specifically why;
(2) only if you're confident which one it is, judge whether it correctly implements
`declared_columns`, exactly like a normal component. If no candidate is plausible (or the
candidate list is empty), that's `AiInconclusive` — stated as such, never forced into a guess at
part (1) just to get to part (2).

## A third, stronger question: left-only gaps

A `## Left-only gaps` section (or a standalone `*.left-only-gap.ai-context.md` file) carries a
different signal than a correlation gap: Tier 1 didn't just fail to correlate here — it actively
**confirmed** no generated counterpart exists at all (a project-wide construct-presence check for
a `Gate3:InternalTransform` scope, a failed schema correlation for `Gate3:SourceOutput`, or a
missing call site/concurrency-group member/failure-handler/event-handler for any `Gate2:*` scope).
Each entry carries:

- **`tier1_detail`** — Tier 1's own explanation of what it looked for and confirmed missing.
- **`declared_evidence`**, when present — whatever left-side shape or semantic evidence Tier 1
  already resolved for this item (declared columns, a Script Component's source, a decoded
  property) — not present for Gate 2 control-flow scopes, which have no schema of their own.
- **`unconfirmed_candidates`** — the same bounded, capped name-mention search used for a
  correlation gap's fallback; none of these are confirmed.

This exists because a confirmed structural gap can still be a false negative for business-logic
equivalence: the generated rewrite may implement the same behavior under a different name or
shape that Tier 1's naming-based correlation cannot see — a "tweaked," non-1:1 rewrite is still a
valid rewrite. Your job here, same two-part shape as a correlation gap: (1) decide whether any
`unconfirmed_candidates` entry is genuinely an unmapped implementation of this item — cite
specifically why, same evidentiary bar as anywhere else in this process; (2) if so, judge it
against `declared_evidence` (when present) for business-logic equivalence. Use the two verdicts
below — never the four from `## Components`/`## Correlation gaps` — for entries in this section:

| Verdict | Meaning |
|---|---|
| `AiFoundUnmappedImplementation` | A candidate under a different name/shape genuinely appears to implement the missing behavior — Tier 1 and Tier 2 disagree. This is the single highest-priority signal for a human to check first: either Tier 1's gap-detection missed a real implementation, or this candidate only looks plausible and isn't actually equivalent. |
| `AiConfirmedGap` | No plausible candidate exists, or a candidate was found but clearly does not implement the missing behavior. Tier 1's gap stands — AI agrees this functionality is genuinely absent from the rewrite. |

Do not reach for `AiFoundUnmappedImplementation` on a weak resemblance (same name fragment, same
data type, "probably related") — that's still `AiConfirmedGap` with the candidate cited and
dismissed, not a forced positive. As always: no candidate evidence to judge from is a valid,
expected outcome, not a failure on your part.

## Required output format

For **each** packet (`## Components`, `## Correlation gaps`, and `## Left-only gaps` alike),
produce:

```
Component: <component_name> (<package>)
Verdict: <one of the six verdicts below>
Rationale: <one paragraph, citing specific evidence from the packet — quote the exact
  FriendlyExpression/property/excerpt line(s) you compared, not a general impression>
```

### Verdict vocabulary (never a bare pass/fail — same discipline as Tier 1)

For `## Components` and `## Correlation gaps` entries:

| Verdict | Meaning |
|---|---|
| `AiConfirmedMatch` | The generated logic, as far as the packet's excerpt shows, implements the same business logic as the SSIS-side evidence. |
| `AiSuspectedDiscrepancy` | You found a specific, citable difference between the two sides — state exactly what differs. |
| `AiInconclusive` | The packet does not contain enough evidence to judge either way (e.g. the excerpt doesn't reach the actual transform logic, or a field you needed is `null`/missing). Say precisely what additional evidence would be needed — do not guess past this. |
| `AiNotApplicable` | This packet's scope doesn't actually require a business-logic judgment (rare — flag it if you think Tier 1 mis-scoped a packet into Tier 2). |

For `## Left-only gaps` entries, use `AiFoundUnmappedImplementation` or `AiConfirmedGap` instead
— see the "left-only gaps" section above; never one of the four verdicts above for these entries.

## Human-in-the-loop — this is advisory, not a verdict

Your output does not get merged into `gate1`/`gate2`/`gate3`'s own reports, and it does not
become a new automatic pass/fail. Save it as `<package>.ai-review.md` alongside the evidence
packets. A human reads your cited evidence and rationale and makes the actual call — an
`AiConfirmedMatch` (or `AiFoundUnmappedImplementation`) is a recommendation for a human to
spot-check, never a promotion to Tier 1's `Match`.

Alongside that prose file, also write a structured sidecar `<package>.ai-review.json`: a flat JSON
list, one object per adjudicated entry, `{"component": <component_name>, "scope": <the packet's
scope string, e.g. "DataFlowComponent"/"CorrelationGap"/"Gate3:InternalTransform">, "verdict":
<the exact verdict string>, "rationale": <the same rationale text>}`. This is what lets a
downstream deterministic summary (the `final-summary` CLI command) merge your verdicts with Tier
1's reports without parsing prose — write it every time, even when every verdict is
`AiInconclusive`/`AiConfirmedGap`.
