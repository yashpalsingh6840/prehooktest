# Repo instructions for GitHub Copilot

This repository is an **independent** SSIS-to-C# rewrite validator. Read `CLAUDE.md` and
`SSIS-Rewrite-Validation-Plan.md` before making changes — they are the authoritative design docs.

## The independence rule (applies to every suggestion you make here)

- Knowledge may come only from Microsoft's public `.dtsx`/SSIS documentation and direct inspection
  of real `.dtsx` and generated `.cs` files — never from the code-generation tool's own internal
  implementation or self-reported limitations.
- Never read, suggest edits to, or reason from `gaps/`, `extract/`, `svk/`, `coverage-report.md`,
  or any generated project's own `README.md`. Those are the code-generation tool's own
  self-reporting about its own correctness; using them would defeat the point of an independent
  validator.
- Gates 1-3 (`ssis_validator/compare/gate{1,2,3}_*.py`) are deterministic, rule-based comparisons.
  Never make them "smarter" by hardcoding tolerance for a known gap in the generation tool, and
  never let an AI judgment call leak into their verdict logic.

## Two-tier validation architecture — where AI fits, and where it does not

- **Tier 1** (Gates 1-3, `ssis_validator validate`): fully deterministic. No AI involved, ever.
  Reports live in `gate_reports/`.
- **Tier 2** (`ssis_validator ai-context` + AI adjudication, see `docs/ai-validation-prompt.md`):
  an optional, clearly separate, advisory-only 4th pass over the small number of findings Tier 1
  genuinely cannot resolve (a business-logic-equivalence judgment, not a structural comparison).
  **Use the `/ssis-tier2-review` prompt file for this** (`.github/prompts/ssis-tier2-review.prompt.md`)
  rather than improvising it in a normal chat — it carries required guardrails.

## Guardrails for any Tier 2 / `ai-context` work specifically

1. The evidence packet (`ai_context/<package>/<package>.ai-context.md`) is the **only** input for
   a Tier 2 judgment. Do not open the `.dtsx`, other generated `.cs` files, or anything else in
   the repo to "double check" — that defeats the packet-scoping this workflow depends on. If more
   evidence is genuinely needed, that means the evidence-packet *builder*
   (`ssis_validator/report/ai_context.py`, `ssis_validator/codegen/csharp_related_source.py`)
   should be extended — not that you should go read the file yourself in the same reply.
2. Never fabricate a match. Missing evidence in the packet is `AiInconclusive`, stated precisely
   — not filled in with an assumption.
3. Tier 2 output is **advisory only**. Never edit `gate_reports/*.json`/`*.md`. Always write a
   Tier 2 result to a new `<package>.ai-review.md` file, never merged into or overwriting Tier 1's
   own reports.
4. Required verdict vocabulary for any Tier 2 judgment: `AiConfirmedMatch` / `AiSuspectedDiscrepancy`
   / `AiInconclusive` / `AiNotApplicable`, each with a rationale that cites specific evidence from
   the packet — never a bare pass/fail.

## Common commands

```
# Deterministic gates (no AI)
python -m ssis_validator.cli validate --dtsx <Package.dtsx> --generated <generated-dir> --out gate_reports

# Tier 2 evidence packet (mechanical — no AI judgment happens in this step)
python -m ssis_validator.cli ai-context --dtsx <Package.dtsx> --generated <generated-dir> --out ai_context/<PackageName> --combined
```

Before rerunning gates, clean `gate_reports/` of its top-level files first (never delete a
user-created archive subfolder like `gate_reports/V1/`).
