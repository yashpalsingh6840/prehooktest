# Tier 2 tooling: Claude Code and GitHub Copilot integration

This file lists every command/skill/prompt file added for using this validator from an AI chat
window, and what guardrail each one enforces. The underlying rules are defined once, in
`docs/ai-validation-prompt.md` — everything here is a thin, tool-specific wrapper around it.

## Why two separate steps

Deliberately split into a **mechanical** step (run the deterministic CLI, no AI judgment, normal
tool access) and a **judgment** step (the actual Tier 2 AI adjudication, tool access restricted).
Keeping them separate means the guardrails only need to constrain the step that actually needs
constraining.

## Claude Code (`.claude/`)

| File | Invocation | Tool access | Job |
|---|---|---|---|
| `commands/ssis-validate.md` | `/ssis-validate <Package>` | Bash (CLI only) + Read | Run Gates 1-3. No AI. |
| `commands/ssis-generate-evidence.md` | `/ssis-generate-evidence <Package>` | Bash (CLI only) + Read | Emit the combined Tier 2 packet. No AI judgment. |
| `commands/ssis-tier2-review.md` | `/ssis-tier2-review <Package>` | **Read + Write only** | The actual AI adjudication. No Bash/Glob/Grep/web — cannot browse anything beyond the packet it's told to read. |
| `skills/ssis-tier2-adjudicate/SKILL.md` | Auto-invoked by natural language ("review the Tier 2 evidence for X", "is LKP_Country implemented correctly") | **Read + Write only** | Same job as the slash command, for when you don't type the exact command name. |

The restricted `allowed-tools: Read, Write` on the review command/skill is the actual permission-
layer guardrail — Claude Code enforces it, not just the written instructions.

## GitHub Copilot (`.github/`)

| File | Invocation | Job |
|---|---|---|
| `copilot-instructions.md` | Automatic (every Copilot Chat request in this repo) | Always-on context: independence rule, the two-tier architecture, common commands, and a pointer to the two files below for Tier 2 work specifically. |
| `prompts/ssis-generate-evidence.prompt.md` | `/ssis-generate-evidence` in Copilot Chat | Runs the CLI (agent mode, terminal tool). Mechanical. |
| `prompts/ssis-tier2-review.prompt.md` | `/ssis-tier2-review` in Copilot Chat | The AI adjudication, `mode: ask` with a minimal `tools` list — Copilot's per-prompt tool restriction is coarser than Claude Code's, so the written rules in the file are the primary guardrail here, not the frontmatter. |
| `chatmodes/ssis-tier2.chatmode.md` | Select the "ssis-tier2" mode from the chat mode dropdown | An alternative to the prompt file: a whole chat mode with no terminal/edit tools at all, for a session where you want that restriction to persist across multiple messages instead of one prompt invocation. |

(Prompt files require the `chat.promptFiles` VS Code setting to be enabled; custom chat modes are
a similarly recent Copilot Chat feature — if either doesn't appear, check your Copilot/VS Code
version, or just paste `docs/ai-validation-prompt.md` plus the packet directly into a normal chat.)

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
