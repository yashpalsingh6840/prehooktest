---
mode: agent
description: Survey an SSIS folder with ssisx extract (read-only, cheap). Minimal tool trips.
---

# ssisx: extract (survey)

Survey the SSIS portfolio at: `${input:inputPath:Absolute path to the SSIS folder, .dtproj, .dtsx or .ispac}`

Write survey output to: `${input:outputPath:Absolute output folder}`

This is the cheap, read-only survey step. Do **not** run `generate` here.

## Known-good constraints — do not re-derive these

- `.github/copilot-instructions.md` is already loaded. **Do not read `COPILOT_GUIDE.md`** —
  it is a large file and this task does not need it.
- Do **not** dump `ssisx --help` (it is ~12KB). There is **no** `ssisx extract --help`
  subcommand — it errors with `unknown extract option '--help'`.
- `ssisx` is not on PATH, and `dotnet` may not be either. Extract only needs `ssisx`; resolve
  `dotnet` only when a build is required. Do not resolve or build `svk` for this prompt.
- **`extract` is the only survey command** — it used to be six separate verbs (`extract`/
  `graph`/`report`/`conformance`/`testgen`/`diff`), merged into one in 2026-09 since none of
  them ever depended on another's output. One call now writes the spec JSON, the inventory/
  findings report, lineage diagrams, gate-1 conformance rules, and gate-2 expression tests,
  all at once. If you ever see a script or an old note run `ssisx report`/`ssisx graph`/etc.
  separately, that's stale — it's just `ssisx extract` now.
- Exit code `2` usually means one or more inputs could not be read (commonly a duplicate
  `DTS:ObjectName`). That is reported, not a crash.

## Step 1 — bootstrap (run this first, once)

```powershell
$cliBin = 'SsisExtractor/src/Ssis.Extract.Cli/bin'
$findExe = { param($dir, $name) Get-ChildItem $dir -Recurse -Include "$name.exe","$name" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName }
$ssisx = & $findExe $cliBin 'ssisx'
if (-not $ssisx) {
  # Extract does not need svk. Build only when the ssisx binary is absent.
  $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
  if (-not $dotnet) {
    $dotnet = @(
      "$env:ProgramFiles/dotnet/dotnet.exe",
      "$env:LOCALAPPDATA/Microsoft/dotnet/dotnet.exe",
      '/usr/share/dotnet/dotnet',
      '/usr/local/share/dotnet/dotnet'
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
  }
  if (-not $dotnet) { throw 'dotnet SDK not found - install it or add it to PATH.' }
    & $dotnet build 'SsisExtractor/SsisExtractor.slnx' -c Debug --nologo
    $ssisx = & $findExe $cliBin 'ssisx'
}
if (-not $ssisx) { throw 'ssisx not found and could not be built.' }
"ssisx  = $ssisx"
```

If `ssisx` is already present, **do not rebuild it**. If the existing binary was found, the
`dotnet` resolution is only informational and may be omitted.

## Step 2 — run this one command

```powershell
& $ssisx extract --input "<inputPath>" --out "<outputPath>" --recursive
$extractExit = $LASTEXITCODE
"EXTRACT_EXIT=$extractExit"
```

Do not claim success until the exit code is printed and `<outputPath>` exists. If the command
was entered into a continuation prompt or its output/exit code was not captured, rerun it in a
fresh non-interactive PowerShell invocation before summarizing.

## Step 3 — summarize, then STOP

Summarize **from the console output only**. Do **not** open `<outputPath>/*.spec.json`,
`<outputPath>/inventory.csv`, `<outputPath>/findings.csv`, or `<outputPath>/portfolio.md` unless I
explicitly ask. Read `<outputPath>/load-failures.md` **only if** the run reported unreadable
inputs.

Report: package names, complexity/score, finding counts, blocking gap counts, dominant gap
kinds, and anything that failed to load and why. Then stop — do not start generating.

## Logging

Append to `session-logs/<yyyy-MM-dd>-session.md` (create it if absent): this prompt, the exact
commands run, their output, and a bump of the request/command counters.
