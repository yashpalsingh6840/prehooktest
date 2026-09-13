---
description: Run Gates 1-3 and emit a combined Tier 2 AI evidence packet for one package (mechanical, deterministic — no judgment).
argument-hint: <PackageName> [--per-component]
allowed-tools: Bash(*.venv/Scripts/python.exe -m ssis_validator.cli:*), Bash(python -m ssis_validator.cli:*), Read
---

Generate the Tier 2 evidence packet for package **$1** (pass `--per-component` as $2 only if the
user explicitly asked for one file per component instead of the combined packet).

This is a purely mechanical step — running the existing, already-independence-audited
`ssis_validator ai-context` CLI. It performs NO judgment and needs NO guardrails beyond "run the
command correctly": Gates 1-3 and the evidence-packet builder are deterministic code, not an AI
judgment call.

Steps:
1. Confirm the package's `.dtsx` path and generated-project path. If not given directly, look for
   them the same way `docs/ai-validation-prompt.md`'s companion commands do — ask the user if
   genuinely ambiguous, never guess a path.
2. Run:
   ```
   <python> -m ssis_validator.cli ai-context --dtsx <path-to-Package.dtsx> --generated <path-to-generated-dir> --out ai_context/<PackageName> --combined
   ```
   (use the project's `.venv` interpreter if one exists at `.venv/Scripts/python.exe`, else `python`).
   Omit `--combined` only if the user passed `--per-component`.
3. Report the output path and the size/roundtrip-savings line the command prints. Do not open or
   summarize the packet's contents yourself here — that is the separate `/ssis-tier2-review`
   step, which carries its own guardrails. This command's job ends at "the packet exists."
