---
mode: 'agent'
description: 'Run Gates 1-3 and emit a combined Tier 2 evidence packet for one SSIS rewrite package (mechanical — no AI judgment).'
---

Generate the Tier 2 evidence packet for the package the user names. This is purely mechanical —
running the existing, deterministic `ssis_validator ai-context` CLI — and needs no independence
guardrails beyond "run the command correctly"; the judgment step is a separate prompt
(`ssis-tier2-review.prompt.md`), which does carry those guardrails.

**Finding the two paths automatically, without guessing:** if the user already gave both the
`.dtsx` path and the generated-project path, use them as-is and skip this. Otherwise, search
rather than ask first:

1. Search the workspace, and any sample-package roots already used elsewhere in this repo (check
   `tests/*.py` for hardcoded `DTSX_DIR`/`GENERATED_ROOT`-style paths if none are obvious in the
   workspace itself), for a file named exactly `<PackageName>.dtsx`.
2. Search the same way for a folder named exactly `<PackageName>` that directly contains a
   `<PackageName>.cs` file — that is the package's *own* generated folder, not the shared parent
   folder that holds every package's subfolder side by side (passing that parent instead is a
   real, previously-hit mistake: it silently either finds no orchestration file at all or mixes in
   every other package's generated code — never pass the parent even if it's what you find first).
3. If exactly one candidate turns up for each, use them and tell the user which paths you picked
   before running anything. If you find zero or more than one plausible candidate for either, stop
   and ask the user for the exact path — never guess between multiple candidates, and never fall
   back to a parent folder just because the exact one wasn't found.

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
.venv\Scripts\python.exe -m ssis_validator.cli ai-context --dtsx <Package.dtsx> --generated <generated-dir> --combined
```

(no `--out` needed — it defaults to `gate_reports`, the same root `/ssis-validate` uses, and the
CLI writes this package's packet under `gate_reports/<PackageName>/ai_context/` on its own. The
CLI itself now also auto-corrects a `--generated` that points one level too high, printing what it
did — but resolving the right path yourself first is still better than relying on that fallback.)

Report the output path (`gate_reports/<PackageName>/ai_context/<PackageName>.ai-context.{json,md}`)
and the packet's reported size/component count. If it reports 0 components + 0 correlation gaps +
0 left-only gaps, say so plainly — that's a valid "nothing left for Tier 2" result, not a failure,
but it's also worth a second look if the user expected findings. Do not open or judge the packet's
contents here — hand off to `/ssis-tier2-review` (or the `ssis-tier2` chat mode) for that.
