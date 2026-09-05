---
mode: agent
description: Run ssisx generate for one named package (or a named few) and triage its gaps.
---

# ssisx: generate one package

- **Input folder:** `${input:inputPath:Absolute path to the SSIS folder/project}`
- **Package(s):** `${input:packages:Package ObjectName(s), comma-separated}`
- **Target framework:** `${input:framework:net8.0 or net10.0 - the CLIENT's runtime}`

If the framework above was left blank or looks like a placeholder, **ask me before generating**.
Do not default to `net10.0` — a build that will not run on the client's runtime is a much worse
failure to find late.

## Known-good constraints — do not re-derive these

- `.github/copilot-instructions.md` is already loaded. **Do not read `COPILOT_GUIDE.md`.**
- Do **not** dump `ssisx --help`.
- **Exit code `3` is expected and is NOT a failure.** It means "wrote code, but there are gaps".
- `--etl-core Etl.Core` and `--fills fills-library` are **mandatory on every call**. Omitting
  `--etl-core` produces a solution that cannot build; omitting `--fills` sends later fill work
  into the disposable `out/` folder where it will be lost.
- `--framework` is **not sticky** — pass it every time.
- Never hand-edit anything under `out/generate/`; it is overwritten on every run.

## Step 1 — bootstrap

Resolve `$ssisx` and `$dotnet` using the bootstrap block in `ssisx-extract.prompt.md`
(Step 1). If `ssisx` already exists, do not rebuild it.

## Step 2 — generate

```powershell
& $ssisx generate --input "<inputPath>" --out out --recursive `
    --package <packages> --etl-core Etl.Core --fills fills-library --framework <framework>
```

## Step 3 — triage the gaps

Read **only** `out/generate-report.md`. Do not open the generated `.cs` files, and do not open
the work packets yet.

Group the gaps by tier and report:

- **Tier 3 (`MissingToolSupport`)** — nothing to fill. Report it as "needs a tool enhancement",
  naming the `GapId`. Do not attempt a workaround.
- **Tier 1 (`MissingDatum`)** — a human must supply a fact. Name the `GapId` and the exact
  question. **Do not invent a plausible value.** Answers go in
  `fills-library/<Package>.decisions.json`.
- **Tier 2 (`MissingLogic`)** — portable by hand. Just count them and name the seams; use the
  `/ssisx-fill` prompt to actually port them.
- **Advisory** — summarize in one line each; these do not block a build.

Note explicitly if the portfolio-level `report` classified something differently from what
`generate` emitted (e.g. a Script Task counted as Tier 3 by `report` but emitted as a Tier-2
seam with a work packet by `generate` — the packet is authoritative).

Then stop. Do not start writing fills unless I ask.

## Logging

Append to `session-logs/<yyyy-MM-dd>-session.md`: this prompt, the exact commands, their
output, the gap table, and a bump of the counters.
