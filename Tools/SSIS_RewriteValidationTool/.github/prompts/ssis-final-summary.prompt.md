---
mode: 'agent'
description: 'Merge Gate 1-3 reports with a Tier 2 ai-review.json (if present) into one Tool-Verified/Exception/AI-Verified/Human-Needs-Review summary for one SSIS rewrite package.'
---

Generate the final Tool/Exception/AI/Human-review summary for the package the user names.

This is a purely mechanical step — running the existing, already-independence-audited
`ssis_validator final-summary` CLI. It performs NO new judgment: it merges Tier 1's own
`gate_reports/<PackageName>/gates/<PackageName>.gate1/2/3.json` verdicts with Tier 2's
`gate_reports/<PackageName>/ai_context/<PackageName>.ai-review.json` (if that file exists — it's
fine if it doesn't, every Unverifiable/LeftOnly finding then correctly falls to
Human-Needs-Review), and buckets every finding into exactly four groups so a human only ever needs
to open the last one. Exception is for findings Tier 1 already structurally confirmed are not a
gap at all (currently: a disabled SSIS executable) — kept separate from Human-Needs-Review because
Tier 1's own per-item conclusion already resolved it.

**Python environment — set it up first if it's missing.** Check whether `.venv/Scripts/python.exe`
exists at the repo root. If it doesn't, create it before doing anything else, per README's own
Setup section:
```
python -m venv .venv
.venv\Scripts\pip install -e ".[dev]"
```
Then always invoke the CLI through `.venv\Scripts\python.exe`, never bare `python`. Only fall back
to bare `python` if venv creation itself fails, and say so plainly rather than silently trying it.

Steps:
1. Confirm `gate_reports/<PackageName>/gates/<PackageName>.gate1.json` and
   `gate_reports/<PackageName>/gates/<PackageName>.gate3.json` exist (Gate 2's report is optional —
   some packages have no orchestration `.cs` file to compare against). If they don't, tell the
   user to run `/ssis-validate` for this package first — do not try to reconstruct gate results
   yourself.
2. Run:
   ```
   .venv\Scripts\python.exe -m ssis_validator.cli final-summary --package <PackageName>
   ```
   (no `--reports-root` needed — it defaults to `gate_reports`, the same root `/ssis-validate` and
   `/ssis-generate-evidence` use.)
3. Report the four printed bucket counts (Tool-Verified / Exception / AI-Verified /
   Human-Needs-Review) and the output paths
   (`gate_reports/<PackageName>/final_review/<PackageName>.final-summary.md` and `.json`). If the
   command notes no `ai-review.json` was found, mention that to the user — running
   `/ssis-tier2-review` first
   would let genuinely-resolved gaps move out of Human-Needs-Review into AI-Verified. Do not open
   or re-summarize the report's contents yourself beyond the printed counts — that's for the user
   to read in the generated file.
