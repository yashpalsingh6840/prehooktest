---
mode: 'agent'
description: 'Run Gates 1-3 and emit a combined Tier 2 evidence packet for one SSIS rewrite package (mechanical — no AI judgment).'
tools: ['codebase', 'terminal']
---

Generate the Tier 2 evidence packet for the package the user names. This is purely mechanical —
running the existing, deterministic `ssis_validator ai-context` CLI — and needs no independence
guardrails beyond "run the command correctly"; the judgment step is a separate prompt
(`ssis-tier2-review.prompt.md`), which does carry those guardrails.

Ask the user for the `.dtsx` path and the generated-project path if not already given (never
guess a path). Then run, using the project's `.venv` interpreter if present:

```
python -m ssis_validator.cli ai-context --dtsx <Package.dtsx> --generated <generated-dir> --out ai_context/<PackageName> --combined
```

Report the output path and the packet's reported size/component count. Do not open or judge the
packet's contents here — hand off to `/ssis-tier2-review` (or the `ssis-tier2` chat mode) for
that.
