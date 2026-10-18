---
mode: 'agent'
description: 'Run Gates 1-3 (deterministic, no AI) for one SSIS rewrite package and report the verdict counts.'
---

Run the deterministic validation gates for the package the user names. No AI judgment happens
here — this is Gate 1 (SQL/schema), Gate 2 (control-flow/data-flow graph), and Gate 3
(per-component I/O schema), all rule-based comparisons with cited evidence, written to
`gate_reports/<PackageName>/gates/<PackageName>.gate{1,2,3}.{json,md}`.

**Finding the two paths automatically, without guessing:** if the user already gave both the
`.dtsx` path and the generated-project path, use them as-is and skip this. Otherwise, search
rather than ask first:

1. Search the workspace, and any sample-package roots already used elsewhere in this repo (check
   `tests/*.py` for hardcoded `DTSX_DIR`/`GENERATED_ROOT`-style paths if none are obvious in the
   workspace itself), for a file named exactly `<PackageName>.dtsx`.
2. Search the same way for a folder named exactly `<PackageName>` that directly contains a
   `<PackageName>.cs` file — that is the package's *own* generated folder, not the shared parent
   folder that holds every package's subfolder side by side (passing that parent instead is a
   real, previously-hit mistake: it silently either finds no orchestration file at all, so Gate 2
   gets skipped, or mixes in every other package's generated code — never pass the parent even if
   it's what you find first).
3. If exactly one candidate turns up for each, use them and tell the user which paths you picked
   before running anything. If you find zero or more than one plausible candidate for either, stop
   and ask the user for the exact path — never guess between multiple candidates, and never fall
   back to a parent folder just because the exact one wasn't found.

Per this repo's own workflow memory: clean `gate_reports/<PackageName>/gates/` before regenerating
(that package's own `gates/` subfolder only — never touch a sibling package's subfolder or a
user-created archive like `gate_reports/V1/`), so old and new runs are never mixed.

**Python environment — set it up first if it's missing.** Check whether `.venv/Scripts/python.exe`
exists at the repo root. If it doesn't, create it before doing anything else, per README's own
Setup section:
```
python -m venv .venv
.venv\Scripts\pip install -e ".[dev]"
```
Then always invoke the CLI through `.venv\Scripts\python.exe`, never bare `python` — a bare
`python` on the user's PATH almost certainly doesn't have `ssis_validator` installed and will fail
with `ModuleNotFoundError`. Only fall back to bare `python` if venv creation itself fails, and say
so plainly rather than silently trying it.

Then run:

```
.venv\Scripts\python.exe -m ssis_validator.cli validate --dtsx <Package.dtsx> --generated <generated-dir>
```
(no `--out` needed — it defaults to `gate_reports`, and this package's Gate 1-3 reports land under
`gate_reports/<PackageName>/gates/`.)

(the CLI itself now also auto-corrects a `--generated` that points one level too high, printing
what it did — but resolving the right path yourself first is still better than relying on that
fallback.)

Report the printed verdict counts for all three gates. If the user wants the Tier 2 AI step for
whatever's left `Unverifiable`/`LeftOnly`, point them at `/ssis-generate-evidence` followed by
`/ssis-tier2-review` — do not perform that judgment here.
