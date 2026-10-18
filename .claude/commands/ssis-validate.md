---
description: Run Gates 1-3 (deterministic, no AI) for one package and report the verdict counts.
argument-hint: <PackageName>
allowed-tools: Bash(*.venv/Scripts/python.exe -m ssis_validator.cli:*), Bash(python -m ssis_validator.cli:*), Bash(python -m venv .venv:*), Bash(.venv/Scripts/pip install:*), Read
---

Run the deterministic validation gates for package **$1**. No AI judgment happens here — this is
Gate 1 (SQL/schema), Gate 2 (control-flow/data-flow graph), and Gate 3 (per-component I/O schema),
all rule-based comparisons with cited evidence.

**Python environment — set it up first if it's missing.** Check whether `.venv/Scripts/python.exe`
exists at the repo root. If it doesn't, create it before doing anything else, per README's own
Setup section:
```
python -m venv .venv
.venv/Scripts/pip install -e ".[dev]"
```
Then always invoke the CLI through `.venv/Scripts/python.exe`, never bare `python` — a bare
`python` on the user's PATH almost certainly doesn't have `ssis_validator` installed and will fail
with `ModuleNotFoundError`. Only fall back to bare `python` if venv creation itself fails, and say
so plainly rather than silently trying it.

Per this repo's own workflow memory: clean `gate_reports/<PackageName>/gates/` before regenerating
(that package's own `gates/` subfolder only — never touch a sibling package's subfolder or a
user-created archive like `gate_reports/V1/`), so old and new runs are never mixed.

Run:
```
.venv/Scripts/python.exe -m ssis_validator.cli validate --dtsx <path-to-Package.dtsx> --generated <path-to-generated-dir>
```
(no `--out` needed — it defaults to `gate_reports`, and this package's Gate 1-3 reports land under
`gate_reports/<PackageName>/gates/`.)

Report the printed verdict counts for all three gates. If the user wants the Tier 2 AI step for
whatever's left `Unverifiable`, point them at `/ssis-generate-evidence` followed by
`/ssis-tier2-review` — do not perform that judgment here.
