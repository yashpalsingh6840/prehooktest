# Tier 2 tooling: Claude Code and GitHub Copilot integration

This file lists every command/skill/prompt file added for using this validator from an AI chat
window, and what guardrail each one enforces. The underlying rules are defined once, in
`docs/ai-validation-prompt.md` — everything here is a thin, tool-specific wrapper around it.

## Why two separate steps

Deliberately split into a **mechanical** step (run the deterministic CLI, no AI judgment, normal
tool access) and a **judgment** step (the actual Tier 2 AI adjudication, tool access restricted).
Keeping them separate means the guardrails only need to constrain the step that actually needs
constraining.

## Sequencing — same four steps, either tool

Both tools expose the identical four-step pipeline; only the invocation differs. See README's
"Commands at a glance, and the order to run them" for the full table with inputs. In short:

| # | Step | Claude Code | GitHub Copilot |
|---|---|---|---|
| 1 | Run Gates 1-3 | `/ssis-validate <Package>` | `/ssis-validate` |
| 2 | Build the Tier 2 evidence packet | `/ssis-generate-evidence <Package>` | `/ssis-generate-evidence` |
| 2b | AI judgment on that packet | `/ssis-tier2-review <Package>` | `/ssis-tier2-review` |
| 3 | Merge into the final rollup | `/ssis-final-summary <Package>` | `/ssis-final-summary` |

Steps 1, 2, and 3 all run the same underlying Python CLI and now all auto-bootstrap `.venv` if
it's missing (create it, `pip install -e ".[dev]"`) before invoking `ssis_validator` — you never
need to run README's Setup section by hand first, though doing so is equivalent. Steps 1 and 2
also auto-discover `--dtsx`/`--generated` from just the package name when there's exactly one
unambiguous match on disk; they ask rather than guess when there isn't.

## Claude Code (`.claude/`)

| File | Invocation | Tool access | Job |
|---|---|---|---|
| `commands/ssis-validate.md` | `/ssis-validate <Package>` | Bash (CLI + venv setup) + Read | Run Gates 1-3. No AI. |
| `commands/ssis-generate-evidence.md` | `/ssis-generate-evidence <Package>` | Bash (CLI + venv setup) + Read/Grep/Glob | Emit the combined Tier 2 packet. No AI judgment. |
| `commands/ssis-tier2-review.md` | `/ssis-tier2-review <Package>` | **Read + Write only** | The actual AI adjudication. No Bash/Glob/Grep/web — cannot browse anything beyond the packet it's told to read. |
| `commands/ssis-final-summary.md` | `/ssis-final-summary <Package>` | Bash (CLI + venv setup) + Read | Merge Tier 1 + Tier 2 into the four-bucket rollup. No AI judgment. |
| `skills/ssis-tier2-adjudicate/SKILL.md` | Auto-invoked by natural language ("review the Tier 2 evidence for X", "is LKP_Country implemented correctly") | **Read + Write only** | Same job as the slash command, for when you don't type the exact command name. |

The restricted `allowed-tools: Read, Write` on the review command/skill is the actual permission-
layer guardrail — Claude Code enforces it, not just the written instructions. The other three
commands' `allowed-tools` is scoped to exactly the CLI invocation plus `python -m venv .venv` /
`.venv/Scripts/pip install` — never an open-ended `Bash(*)`.

## GitHub Copilot (`.github/`)

| File | Invocation | Job |
|---|---|---|
| `copilot-instructions.md` | Automatic (every Copilot Chat request in this repo) | Always-on context: independence rule, the two-tier architecture, common commands, and a pointer to the files below for Tier 2 work specifically. |
| `prompts/ssis-validate.prompt.md` | `/ssis-validate` in Copilot Chat | Runs Gates 1-3 (agent mode, terminal tool). Writes `gate_reports/<Package>/`. No AI. |
| `prompts/ssis-generate-evidence.prompt.md` | `/ssis-generate-evidence` in Copilot Chat | Runs the CLI (agent mode, terminal tool). Writes `gate_reports/<Package>/ai_context/`. Mechanical. |
| `prompts/ssis-tier2-review.prompt.md` | `/ssis-tier2-review` in Copilot Chat | The AI adjudication, `mode: edit` (file edits only, so it can save `.ai-review.{md,json}` itself — no terminal/web) — Copilot's per-prompt tool restriction is coarser than Claude Code's, so the written rules in the file are the primary guardrail here, not the frontmatter. |
| `prompts/ssis-final-summary.prompt.md` | `/ssis-final-summary` in Copilot Chat | Runs the CLI (agent mode, terminal tool). Merges Tier 1 + Tier 2 into the four-bucket rollup. No AI. |
| `chatmodes/ssis-tier2.chatmode.md` | Select the "ssis-tier2" mode from the chat mode dropdown | An alternative to the prompt file: a whole chat mode with no terminal/edit tools at all, for a session where you want that restriction to persist across multiple messages instead of one prompt invocation. |

(Prompt files require the `chat.promptFiles` VS Code setting to be enabled; custom chat modes are
a similarly recent Copilot Chat feature — if either doesn't appear, check your Copilot/VS Code
version, or just paste `docs/ai-validation-prompt.md` plus the packet directly into a normal chat.)
None of the three Copilot agent-mode prompts restrict `tools:` in frontmatter (an earlier version
of `ssis-generate-evidence.prompt.md` did, using an invalid tool id, which silently left it with no
terminal access at all — see git history 2026-09-13 — so this repo now deliberately leaves agent
mode's default toolset alone here rather than re-guessing another tool id).

## The guardrails, restated once

Every file above ultimately enforces the same five rules (see `docs/ai-validation-prompt.md` for
the full version):

1. The evidence packet is the only input for a Tier 2 judgment — never the `.dtsx`, never other
   generated `.cs` files, and never `gaps/`/`extract/`/`svk/`/`coverage-report.md`/a generated
   project's own `README.md`.
2. Missing evidence is `AiInconclusive`, stated precisely — never guessed.
3. Output is one `Component / Verdict / Rationale` block per component, verdict one of
   `AiConfirmedMatch` / `AiSuspectedDiscrepancy` / `AiInconclusive` / `AiNotApplicable`.
4. Advisory only — never edit `gate_reports/`; always write a new `<package>.ai-review.md`.
5. A request to "just check the real code to be sure" is declined, not honored — if evidence is
   genuinely missing, the fix is extending the evidence-packet builder
   (`ssis_validator/report/ai_context.py`), not a manual peek that breaks the audit trail.
