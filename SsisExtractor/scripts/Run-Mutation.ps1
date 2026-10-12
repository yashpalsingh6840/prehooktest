<#
.SYNOPSIS
    Applies ONE mutation (a byte-preserving find/replace) to a generated source file, runs that
    package's own generated test project against it, records the result, then reverts the file
    unconditionally -- whether the test run passed, failed, or crashed.

.DESCRIPTION
    The primitive `Run-MutationSuite.ps1` calls once per row of its own mutation table (Phase 0.3/
    Phase 6 of the independent-review fix plan, `C:\Users\yashpal.singh01\.claude\plans\
    snappy-skipping-grove.md`). Kept as its own single-purpose script -- not folded into the
    suite driver -- so a single mutation can be reproduced by hand from the command line too.

    "Killed" means the mutated build/test run FAILED (build error or a red test) -- i.e. the
    starter tests this tool generates caught the planted bug. "Survived" means it built and
    passed anyway -- the starter tests missed it.

.PARAMETER Id
    A short, log-file-safe identifier for this mutation (e.g. "M1-swapFirstLast").

.PARAMETER TestProj
    Full path to the generated `*.Tests.csproj` to run after the mutation is applied.

.PARAMETER File
    Full path to the generated source file to mutate.

.PARAMETER Old
    The exact substring to replace. If it is not found in the file verbatim, the mutation is
    reported NOT-APPLIED (never silently skipped) and nothing is run or reverted.

.PARAMETER New
    The replacement text.

.PARAMETER LogDir
    Directory to write `<Id>.log` (the full `dotnet test` transcript) into. Created if missing.
    No default -- the caller (normally `Run-MutationSuite.ps1`) decides where its own run's logs
    live; this script never assumes a scratch path outside the repo.

.OUTPUTS
    Writes one summary line to the pipeline: "<Id> exit=<code> killed=<True|False> :: <summary>".
    Never throws on a red test/build -- that is the expected, useful outcome of a kill.
#>
param(
    [Parameter(Mandatory)][string]$Id,
    [Parameter(Mandatory)][string]$TestProj,
    [Parameter(Mandatory)][string]$File,
    [Parameter(Mandatory)][string]$Old,
    # AllowEmptyString: several real mutations (e.g. "drop the Salary assignment") replace the
    # matched text with nothing at all -- PowerShell's Mandatory validation otherwise rejects an
    # empty string as "no value provided", even though "" is exactly the intended replacement here.
    [Parameter(Mandatory)][AllowEmptyString()][string]$New,
    [Parameter(Mandatory)][string]$LogDir
)
$ErrorActionPreference = 'Continue'

if (-not (Test-Path $File)) {
    "$Id NOT-APPLIED (file missing: $File)"
    return
}
if (-not (Test-Path $TestProj)) {
    "$Id NOT-APPLIED (test project missing: $TestProj)"
    return
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# Byte-preserving replace (CLAUDE.md's own standing rule for any generated/checked-in text
# file -- a plain PowerShell -replace on text read via Get-Content can re-encode line endings).
$origBytes = [IO.File]::ReadAllBytes($File)
$orig = [Text.Encoding]::UTF8.GetString($origBytes)
if (-not $orig.Contains($Old)) {
    "$Id NOT-APPLIED (pattern missing in $([IO.Path]::GetFileName($File)))"
    return
}
$idx = $orig.IndexOf($Old)
$mutated = $orig.Substring(0, $idx) + $New + $orig.Substring($idx + $Old.Length)
[IO.File]::WriteAllBytes($File, [Text.Encoding]::UTF8.GetBytes($mutated))

$log = Join-Path $LogDir "$Id.log"
try {
    dotnet test $TestProj -c Release -nodeReuse:false --filter "Category!=Integration" *> $log
    $code = $LASTEXITCODE
}
finally {
    # Revert unconditionally, even if dotnet test itself crashed the process.
    [IO.File]::WriteAllBytes($File, $origBytes)
}

$killed = $code -ne 0
$sum = (Select-String $log -Pattern 'Passed!|Failed!|error CS|Build FAILED' -ErrorAction SilentlyContinue |
        Select-Object -First 2 | ForEach-Object Line) -join ' | '
"$Id exit=$code killed=$killed :: $sum"
