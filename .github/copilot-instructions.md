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
  Reports live in `gate_reports/`. **Use the `/ssis-validate` prompt file for this**
  (`.github/prompts/ssis-validate.prompt.md`) rather than typing the command by hand each time.
- **Tier 2** (`ssis_validator ai-context` + AI adjudication, see `docs/ai-validation-prompt.md`):
  an optional, clearly separate, advisory-only 4th pass over the small number of findings Tier 1
  genuinely cannot resolve (a business-logic-equivalence judgment, not a structural comparison).
  **Use the `/ssis-tier2-review` prompt file for this** (`.github/prompts/ssis-tier2-review.prompt.md`)
  rather than improvising it in a normal chat — it carries required guardrails.

## Output layout — one folder per package

All three subcommands share one reports root (`gate_reports/` by default) and lay results out as
one folder per package under it — never edit these paths by hand, the CLI derives them:

```
gate_reports/<Package>/gates/<Package>.gate{1,2,3}.{json,md}     (validate)
gate_reports/<Package>/ai_context/<Package>.ai-context.{json,md} (ai-context, --combined)
gate_reports/<Package>/ai_context/<Package>.ai-review.{json,md}  (Tier 2 review output)
gate_reports/<Package>/gaps/*.correlation-gap.ai-context.*       (ai-context, non-combined only)
gate_reports/<Package>/gaps/*.left-only-gap.ai-context.*         (ai-context, non-combined only)
gate_reports/<Package>/final_review/<Package>.final-summary.{json,md}  (final-summary)
```

## Guardrails for any Tier 2 / `ai-context` work specifically

1. The evidence packet (`gate_reports/<package>/ai_context/<package>.ai-context.md`) is the
   **only** input for a Tier 2 judgment. Do not open the `.dtsx`, other generated `.cs` files, or
   anything else in the repo to "double check" — that defeats the packet-scoping this workflow
   depends on. If more evidence is genuinely needed, that means the evidence-packet *builder*
   (`ssis_validator/report/ai_context.py`, `ssis_validator/codegen/csharp_related_source.py`)
   should be extended — not that you should go read the file yourself in the same reply.
2. Never fabricate a match. Missing evidence in the packet is `AiInconclusive`, stated precisely
   — not filled in with an assumption.
3. Tier 2 output is **advisory only**. Never edit
   `gate_reports/<package>/gates/<package>.gate{1,2,3}.json`/`.md`. Always write a Tier 2 result to
   a new `gate_reports/<package>/ai_context/<package>.ai-review.md` file, never merged into or
   overwriting Tier 1's own reports.
4. Required verdict vocabulary for any Tier 2 judgment: `AiConfirmedMatch` / `AiSuspectedDiscrepancy`
   / `AiInconclusive` / `AiNotApplicable`, each with a rationale that cites specific evidence from
   the packet — never a bare pass/fail.

## Common commands

```
# Python environment: create it first if `.venv/Scripts/python.exe` doesn't exist yet
python -m venv .venv
.venv\Scripts\pip install -e ".[dev]"

# Deterministic gates (no AI) -- writes gate_reports/<PackageName>/gates/
.venv\Scripts\python.exe -m ssis_validator.cli validate --dtsx <Package.dtsx> --generated <generated-dir>

# Tier 2 evidence packet (mechanical — no AI judgment happens in this step) -- writes
# gate_reports/<PackageName>/ai_context/
.venv\Scripts\python.exe -m ssis_validator.cli ai-context --dtsx <Package.dtsx> --generated <generated-dir> --combined

# Final rollup (merges the two above; needs neither run first, but is most useful after both)
.venv\Scripts\python.exe -m ssis_validator.cli final-summary --package <PackageName>
```

None of the three commands require `--out`/`--reports-root` — they all default to `gate_reports`.
Before rerunning gates for one package, clean `gate_reports/<PackageName>/gates/` (that package's
own `gates/` subfolder only — never delete a sibling package's subfolder or a user-created archive
like `gate_reports/V1/`).
