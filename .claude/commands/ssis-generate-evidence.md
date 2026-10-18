---
description: Run Gates 1-3 and emit a combined Tier 2 AI evidence packet for one package (mechanical, deterministic — no judgment).
argument-hint: <PackageName> [--per-component]
allowed-tools: Bash(*.venv/Scripts/python.exe -m ssis_validator.cli:*), Bash(python -m ssis_validator.cli:*), Bash(python -m venv .venv:*), Bash(.venv/Scripts/pip install:*), Read, Grep, Glob
---

Generate the Tier 2 evidence packet for package **$1** (pass `--per-component` as $2 only if the
user explicitly asked for one file per component instead of the combined packet).

This is a purely mechanical step — running the existing, already-independence-audited
`ssis_validator ai-context` CLI. It performs NO judgment and needs NO guardrails beyond "run the
command correctly": Gates 1-3 and the evidence-packet builder are deterministic code, not an AI
judgment call.

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

Steps:
1. **Find the two paths automatically, without guessing.** If the user already gave both the
   `.dtsx` path and the generated-project path, use them as-is. Otherwise:
   - Search the workspace (and `tests/*.py` for hardcoded `DTSX_DIR`/`GENERATED_ROOT`-style sample
     roots if nothing's obvious in the workspace itself) for a file named exactly
     `<PackageName>.dtsx`.
   - Search the same way for a folder named exactly `<PackageName>` that directly contains a
     `<PackageName>.cs` file — the package's *own* generated folder, never the shared parent
     folder that holds every package's subfolder side by side (a real, previously-hit mistake:
     passing the parent silently either finds no orchestration file at all or mixes in every other
     package's generated code).
   - If exactly one candidate turns up for each, use them and tell the user which paths you picked
     before running anything. If zero or more than one plausible candidate turns up for either,
     stop and ask the user for the exact path — never guess between candidates.
2. Run:
   ```
   .venv/Scripts/python.exe -m ssis_validator.cli ai-context --dtsx <path-to-Package.dtsx> --generated <path-to-generated-dir> --combined
   ```
   (no `--out` needed — it defaults to `gate_reports`, the same root `/ssis-validate` uses, and
   the CLI writes this package's packet under `gate_reports/<PackageName>/ai_context/` on its own.
   The CLI itself now also auto-corrects a `--generated` that points one level too high, printing
   what it did — but resolving the right path yourself first is still better than relying on that
   fallback.) Omit `--combined` only if the user passed `--per-component`.
3. Report the output path (`gate_reports/<PackageName>/ai_context/<PackageName>.ai-context.{json,md}`)
   and the size/roundtrip-savings line the command prints. If it reports 0 components + 0
   correlation gaps + 0 left-only gaps, say so plainly — a valid "nothing left for Tier 2" result,
   not a failure, but worth a second look if the user expected findings. Do not open or summarize
   the packet's contents yourself here — that is the separate `/ssis-tier2-review` step, which
   carries its own guardrails. This command's job ends at "the packet exists."
