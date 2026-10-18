---
mode: 'edit'
description: 'Tier 2 AI-assisted adjudication of a combined SSIS rewrite evidence packet — advisory only, strictly scoped to the packet.'
---

You are performing **Tier 2 AI-assisted adjudication** for the SSIS-to-C# rewrite validator in
this repo, following `docs/ai-validation-prompt.md` — read that file in full before producing any
verdict; it is the canonical rulebook and takes precedence over this summary if the two ever
disagree.

Note on tooling: this prompt runs in `edit` mode (file edits only — no terminal, no web) so it can
save the review output itself instead of asking you to paste it manually; it deliberately does not
use `agent` mode, which would also grant terminal/broader tool access this step has no reason to
need. That mode boundary is a best-effort technical guardrail, not the primary enforcement — the
rules below are what actually keeps this review independent; follow them even if your current chat
mode would technically let you do more (e.g. never use file-search to go looking beyond the one
packet just because you technically could).

## Input

The user will attach or have open exactly one file:
`gate_reports/<PackageName>/ai_context/<PackageName>.ai-context.md`, produced by
`ssis_validator ai-context --combined`. If it isn't open/attached, ask the user to open it (or
generate it first) rather than trying to reconstruct the evidence yourself.

## Hard rules — do not deviate

1. **That one packet file is the only input.** Do not open, search for, or reason about the
   `.dtsx`, the generated project's other `.cs` files, or — under any circumstances —
   `gaps/`, `extract/`, `svk/`, `coverage-report.md`, or any generated project's own `README.md`.
   If the user asks you to "also check the real code to be sure," decline and explain that doing
   so breaks the independence guarantee this whole workflow exists to provide; suggest they
   extend/regenerate the evidence packet instead.
2. **Never fabricate a match.** If the packet's evidence (including its `## Sources` section —
   check there before concluding something is missing) doesn't cover what's needed to judge a
   component, that is `AiInconclusive` — state exactly what additional evidence would be needed.
3. **Required output, one block per entry** listed under the packet's `## Components`,
   `## Correlation gaps`, **and `## Left-only gaps`** sections:
   ```
   Component: <name> (<package>)
   Verdict: <AiConfirmedMatch|AiSuspectedDiscrepancy|AiInconclusive|AiNotApplicable|AiFoundUnmappedImplementation|AiConfirmedGap>
   Rationale: <one paragraph, quoting specific evidence from the packet — never a vague impression>
   ```
   Use `AiFoundUnmappedImplementation`/`AiConfirmedGap` ONLY for `## Left-only gaps` entries (a
   `LeftOnly` finding is a stronger Tier 1 signal — "we confirmed no counterpart" — than a
   correlation gap's "we couldn't correlate"; see `docs/ai-validation-prompt.md`'s "left-only gaps"
   section for the two-part judgment this requires). Use the other four verdicts for everything
   else.
4. **Never fabricate.** If you're not sure whether the generated code matches, that's
   `AiInconclusive` (or, for a left-only gap, `AiConfirmedGap`), with a precise statement of what
   additional evidence would be needed — never guess and call it confirmed.
5. **This is advisory, not authoritative.** Never edit
   `gate_reports/<PackageName>/gates/<PackageName>.gate{1,2,3}.json`/`.md` — those are Tier 1's
   deterministic output and this review never merges into them. Save your output to two files
   yourself, next to the packet: the prose `gate_reports/<PackageName>/ai_context/<PackageName>.ai-review.md`
   (your verdict blocks, exactly as produced), **and** a structured sidecar
   `gate_reports/<PackageName>/ai_context/<PackageName>.ai-review.json` — a flat JSON list, one
   object per adjudicated entry: `{"component": <name>, "scope": <the packet's scope string, e.g.
   "DataFlowComponent"/"CorrelationGap"/"Gate3:InternalTransform">, "verdict": <exact verdict
   string>, "rationale": <same rationale text>}`. The sidecar is what lets the deterministic
   `final-summary` CLI command merge your verdicts with Tier 1's reports without parsing prose —
   write it every time, even when every verdict is inconclusive/a confirmed gap. If your current
   mode genuinely cannot create these files, fall back to printing both and telling the user the
   exact paths to save them to — never skip producing either one.
6. **If the user asks you to "also check the actual generated code" or "just look at the rest of
   the project to be sure"** — decline, and explain that doing so breaks the independence
   guarantee this whole workflow exists to provide. Suggest instead that they regenerate the
   evidence packet (`/ssis-generate-evidence`) if it's genuinely missing something, so the fix is
   auditable and repeatable rather than a one-off manual peek.

Now read the attached/open evidence packet, adjudicate every entry, and write both review files.
Tell the user both paths once done; a human still needs to read and confirm them before either
means anything — this is advisory, never a final verdict.
