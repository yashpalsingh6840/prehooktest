---
mode: agent
description: Survey an SSIS folder with ssisx extract + report (read-only, cheap). Minimal tool trips.
---

# ssisx: extract + report

Survey the SSIS portfolio at: `${input:inputPath:Absolute path to the SSIS folder, .dtproj, .dtsx or .ispac}`

This is the cheap, read-only survey step. Do **not** run `generate` here.

## Known-good constraints — do not re-derive these

- `.github/copilot-instructions.md` is already loaded. **Do not read `COPILOT_GUIDE.md`** —
  it is a large file and this task does not need it.
- Do **not** dump `ssisx --help` (it is ~12KB). There is **no** `ssisx extract --help`
  subcommand — it errors with `unknown extract option '--help'`.
- `ssisx` is not on PATH, and `dotnet` may not be either. Resolve both with the bootstrap
  below rather than assuming a location.
- Exit code `2` from `report` usually means one or more inputs could not be read
  (commonly a duplicate `DTS:ObjectName`). That is reported, not a crash.

## Step 1 — bootstrap (run this first, once)

```powershell
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

$cliBin = 'SsisExtractor/src/Ssis.Extract.Cli/bin'
$find   = { Get-ChildItem $cliBin -Recurse -Include 'ssisx.exe','ssisx' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName }
$ssisx  = & $find
if (-not $ssisx) {
    & $dotnet build 'SsisExtractor/SsisExtractor.slnx' -c Debug --nologo
    $ssisx = & $find
}
if (-not $ssisx) { throw 'ssisx not found and could not be built.' }
"ssisx  = $ssisx"
"dotnet = $dotnet"
```

If `ssisx` was already present, **do not rebuild it**.

## Step 2 — run exactly these two commands

```powershell
& $ssisx extract --input "<inputPath>" --out out --recursive
& $ssisx report  --input "<inputPath>" --out out --recursive
```

## Step 3 — summarize, then STOP

Summarize **from the console output only**. Do **not** open `out/*.spec.json`,
`out/inventory.csv`, `out/findings.csv`, or `out/portfolio.md` unless I explicitly ask.
Read `out/load-failures.md` **only if** the run reported unreadable inputs.

Report: package names, complexity/score, finding counts, blocking gap counts, dominant gap
kinds, and anything that failed to load and why. Then stop — do not start generating.

## Logging

Append to `session-logs/<yyyy-MM-dd>-session.md` (create it if absent): this prompt, the exact
commands run, their output, and a bump of the request/command counters.
