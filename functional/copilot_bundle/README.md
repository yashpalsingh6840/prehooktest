# DI Functional Baseline — Copilot bundle

Credit-saving pipeline for populating `DI_Repository_Functional_Baseline_*.docx`.
Scripts do everything mechanical; Copilot only reasons about business logic.

| File | Runs as | Purpose |
|---|---|---|
| `discover.py` | script | Scans the .NET repo once: Docs folder, entry points (WebAPI/WCF/BatchJob), SSIS package matches. Zero model cost. |
| `schemas.json` | data | Maps each Word template table to its columns/ID prefix. Shared by the prompt and the merge script so both agree on shape. |
| `merge_to_docx.py` | script | Appends a Copilot-produced facts JSON into the real Word tables (auto-numbers IDs, preserves formatting). Zero model cost. |
| `PROMPT.md` | paste into Copilot | The one prompt that drives discovery → per-entry-point analysis → merge, entry-point by entry-point instead of whole-repo-at-once. |

## Quick start

1. Open the target .NET repo in VS Code with Copilot Chat in Agent mode.
2. Copy this `copilot_bundle/` folder and the baseline `.docx` template into the repo (or reference them by path).
3. Open `PROMPT.md`, fill in the CONFIG block at the top (repo path, SSIS metadata root if any, docx path).
4. Paste the whole prompt into Copilot Chat and let it run.

## Why this is cheaper than the original single mega-prompt

- Discovery and merging are deterministic Python, not model calls.
- Copilot only ever sees the file list for *one entry point* at a time, not the whole repo or the whole document template.
- SSIS-backed batch jobs reuse your pre-generated `svk walkthrough` output instead of asking Copilot to re-derive control-flow/data-flow from `.dtsx` XML.
- IDs, table structure, and formatting are never re-negotiated by the model — `merge_to_docx.py` guarantees them.

## Scope note

Currently targets .NET repos (WebAPI / WCF / console batch jobs, optionally
SSIS-triggered). Extend `discover.py`'s classifiers if/when other stacks need
the same treatment.
