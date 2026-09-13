---
description: Run Gates 1-3 (deterministic, no AI) for one package and report the verdict counts.
argument-hint: <PackageName>
allowed-tools: Bash(*.venv/Scripts/python.exe -m ssis_validator.cli:*), Bash(python -m ssis_validator.cli:*), Read
---

Run the deterministic validation gates for package **$1**. No AI judgment happens here — this is
Gate 1 (SQL/schema), Gate 2 (control-flow/data-flow graph), and Gate 3 (per-component I/O schema),
all rule-based comparisons with cited evidence.

Per this repo's own workflow memory: clean `gate_reports/` of top-level files (never touch a
`gate_reports/V1/`-style archive subfolder if the user has one) before regenerating, so old and
new runs are never mixed.

Run:
```
<python> -m ssis_validator.cli validate --dtsx <path-to-Package.dtsx> --generated <path-to-generated-dir> --out gate_reports
```
using the project's `.venv` interpreter if present. Report the printed verdict counts for all
three gates. If the user wants the Tier 2 AI step for whatever's left `Unverifiable`, point them
at `/ssis-generate-evidence` followed by `/ssis-tier2-review` — do not perform that judgment here.
