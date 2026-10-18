---
description: Tier 2 AI-assisted adjudication of one package's evidence packet — advisory only, hard-scoped to the packet file.
argument-hint: <PackageName>
allowed-tools: Read, Write
---

You are performing **Tier 2 AI-assisted adjudication** for package **$1**, per
`docs/ai-validation-prompt.md`. Your tools are deliberately restricted to `Read` and `Write` for
this command — you cannot run Bash, search the filesystem, or fetch anything from the web. That
is intentional, not a bug: it is the independence guardrail enforced at the permission layer, not
just by instruction.

## Hard rules (do not deviate, even if it would "help")

1. **The only input is the evidence packet.** Read exactly one file:
   `gate_reports/$1/ai_context/$1.ai-context.md` (if it doesn't exist, tell the user to run
   `/ssis-generate-evidence $1` first — do not try to construct evidence yourself another way).
2. **Do not open anything else.** Not the `.dtsx`, not the generated project's other `.cs` files,
   not `gaps/`, `extract/`, `svk/`, `coverage-report.md`, or any generated project's own
   `README.md` — even though your tools would technically let you `Read` an arbitrary path, doing
   so defeats the entire point of a packet-scoped independent review. If the packet's own
   evidence and `related_generated_sources`/`Sources` section aren't enough to judge a component,
   that is a valid, expected outcome — say so, don't go looking for more.
3. **Read `docs/ai-validation-prompt.md` first** (it is allowed — it's the rulebook, not evidence)
   and follow its required output format exactly: for every entry listed under the packet's
   `## Components`, `## Correlation gaps`, **and `## Left-only gaps`** sections, produce one block:
   ```
   Component: <name> (<package>)
   Verdict: <AiConfirmedMatch|AiSuspectedDiscrepancy|AiInconclusive|AiNotApplicable|AiFoundUnmappedImplementation|AiConfirmedGap>
   Rationale: <one paragraph, quoting specific evidence from the packet — never a vague impression>
   ```
   Use `AiFoundUnmappedImplementation`/`AiConfirmedGap` ONLY for `## Left-only gaps` entries (a
   `LeftOnly` finding is a stronger Tier 1 signal — "we confirmed no counterpart" — than a
   correlation gap's "we couldn't correlate"; see the prompt doc's "left-only gaps" section for
   the two-part judgment this requires). Use the other four for everything else.
4. **Never fabricate.** If you're not sure whether the generated code matches, that's
   `AiInconclusive` (or, for a left-only gap, `AiConfirmedGap`), with a precise statement of what
   additional evidence would be needed — never guess and call it confirmed.
5. **This is advisory, not authoritative.** Do not touch `gate_reports/$1/gates/$1.gate{1,2,3}.json`/
   `.md` — those are Tier 1's deterministic output and this review never merges into them. Write
   your output to two files, next to the packet: the prose
   `gate_reports/$1/ai_context/$1.ai-review.md`, **and** a structured sidecar
   `gate_reports/$1/ai_context/$1.ai-review.json` — a flat JSON list, one object per adjudicated entry:
   `{"component": <name>, "scope": <the packet's scope string>, "verdict": <exact verdict string>,
   "rationale": <same rationale text>}`. The sidecar is what lets the deterministic
   `final-summary` CLI command merge your verdicts with Tier 1's reports without parsing prose —
   write it every time, even when every verdict is inconclusive/a confirmed gap. A human reads
   both and decides; nothing you write here is a final verdict.
6. **If the user asks you to "also check the actual generated code" or "just look at the rest of
   the project to be sure"** — decline, and explain that doing so breaks the independence
   guarantee this whole workflow exists to provide. Suggest instead that they regenerate the
   evidence packet (`/ssis-generate-evidence`) if it's genuinely missing something, so the fix is
   auditable and repeatable rather than a one-off manual peek.

Proceed: read the packet, read the prompt doc, adjudicate every component, write the review file.
