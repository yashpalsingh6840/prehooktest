<#
.SYNOPSIS
    Clone every repo of a GitHub org/user into one folder.

.DESCRIPTION
    Lists the repos through the GitHub API, then clones them (in parallel on PowerShell 7+).
    Defaults are tuned for *scanning* the code, not for development:
      * shallow, single-branch clones (latest commit of the default branch only) - small and fast
      * archived repos and forks are skipped (use -IncludeArchived / -IncludeForks to include them)
      * LFS files are not downloaded
      * already-cloned repos are left alone (use -Update to refresh them; repos with local changes are never touched)

    Works with github.com and GitHub Enterprise Server. A token is only needed for private/internal repos
    (or to avoid the 60 requests/hour unauthenticated API limit). The token is passed to git through the environment
    for this run only - it is never written into .git/config or the command line.

.PARAMETER BaseUrl     Org or user URL, e.g. https://github.com/contoso  (GHE: https://git.contoso.com/platform)
.PARAMETER TargetPath  Folder to clone into, e.g. C:\Proj  (created if missing). Each repo gets its own sub-folder.
.PARAMETER Token       GitHub token (default: $env:GITHUB_TOKEN). Fine-grained: Contents = Read-only on the repos. Classic: 'repo'.

.EXAMPLE
    .\Clone-GitHubRepos.ps1 -BaseUrl https://github.com/contoso -TargetPath C:\Proj
.EXAMPLE
    .\Clone-GitHubRepos.ps1 -BaseUrl https://github.com/contoso -TargetPath C:\Proj -Exclude 'docs-*','*-archive' -Update
.EXAMPLE
    .\Clone-GitHubRepos.ps1 -BaseUrl https://github.com/contoso -TargetPath C:\Proj -ListOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$TargetPath,
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$ApiBase,
    # Only repos whose name matches one of these wildcards / none of the -Exclude wildcards
    [string[]]$Include = @('*'),
    [string[]]$Exclude = @(),
    [switch]$IncludeArchived,
    [switch]$IncludeForks,
    # 1 = latest commit only (default). 0 = full history.
    [int]$Depth = 1,
    [int]$Parallel = 4,
    [switch]$Update,
    [switch]$ListOnly
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "git was not found on PATH." }

# ---- URL / API
$u = [uri]$BaseUrl
$org = ($u.AbsolutePath.Trim('/') -split '/')[0]
if (-not $org) { throw "Could not read an org/user name from '$BaseUrl' (expected e.g. https://github.com/contoso)." }
if ($ApiBase) { $api = $ApiBase } elseif ($u.Host -eq 'github.com') { $api = 'https://api.github.com' } else { $api = "$($u.Scheme)://$($u.Host)/api/v3" }
$api = $api.TrimEnd('/')

$headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'endpoint-scan'; 'X-GitHub-Api-Version' = '2022-11-28' }
if ($Token) { $headers.Authorization = "Bearer $Token" }

function Invoke-GhList([string]$path) {
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try { return (Invoke-RestMethod -Uri $path -Headers $headers -Method Get -ErrorAction Stop) }
        catch {
            $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
            if ($code -eq 404) { return $null }
            if ($code -eq 401) { if ($Token) { throw "GitHub returned 401 - the token is invalid or expired." } else { throw "GitHub requires authentication for '$org' - pass -Token or set `$env:GITHUB_TOKEN." } }
            if ($code -in 403, 429 -and $attempt -lt 4) { Write-Host "  API rate limit, waiting 60 s ..."; Start-Sleep -Seconds 60; continue }
            if ($code -ge 500 -and $attempt -lt 4) { Start-Sleep -Seconds (2 * $attempt); continue }
            throw
        }
    }
}

# ---- list repos
$kind = 'orgs'
if ($null -eq (Invoke-GhList "$api/orgs/$org")) { $kind = 'users' }
$all = New-Object System.Collections.Generic.List[object]
$page = 1
while ($true) {
    $items = @(Invoke-GhList "$api/$kind/$org/repos?per_page=100&page=$page&type=all")
    if ($items.Count -eq 0 -or $null -eq $items[0]) { break }
    foreach ($x in $items) { $all.Add($x) }
    if ($items.Count -lt 100) { break }; $page++
}
if ($all.Count -eq 0) { throw "No repos returned for '$org'. Check the URL, and that the token can see the org's repos (SSO-protected orgs need the token authorised)." }

$skipArchived = 0; $skipFork = 0; $skipFilter = 0
$repoList = New-Object System.Collections.Generic.List[object]
foreach ($x in ($all | Sort-Object name)) {
    if (-not $IncludeArchived -and $x.archived) { $skipArchived++; continue }
    if (-not $IncludeForks -and $x.fork) { $skipFork++; continue }
    $ok = $false; foreach ($p in $Include) { if ($x.name -like $p) { $ok = $true; break } }
    foreach ($p in $Exclude) { if ($x.name -like $p) { $ok = $false; break } }
    if (-not $ok) { $skipFilter++; continue }
    $repoList.Add([pscustomobject]@{ Name = $x.name; CloneUrl = $x.clone_url; Private = [bool]$x.private })
}
$skippedText = @(); if ($skipArchived) { $skippedText += "$skipArchived archived" }; if ($skipFork) { $skippedText += "$skipFork forks" }; if ($skipFilter) { $skippedText += "$skipFilter by filter" }
Write-Host ("{0}: {1} repos found, {2} to clone{3}" -f $org, $all.Count, $repoList.Count, $(if ($skippedText) { " (skipped: $($skippedText -join ', '))" } else { '' }))
if ($ListOnly) { $repoList | ForEach-Object { $_.Name }; return }
if ($repoList.Count -eq 0) { return }
if (@($repoList | Where-Object Private).Count -gt 0 -and -not $Token) { Write-Warning "Private repos are in the list but no token was given - their clones will fail." }

New-Item -ItemType Directory -Force -Path $TargetPath | Out-Null
$TargetPath = (Resolve-Path -LiteralPath $TargetPath).Path

# ---- worker (runs once per repo; same code for sequential and parallel mode)
$workerText = @'
param($r, $target, $depth, $update)
$res = [ordered]@{ Name = $r.Name; Status = ''; Detail = '' }
$dest = Join-Path $target $r.Name
function Last-Line($o) {
    $l = @($o | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $f = @($l | Where-Object { $_ -match '^(fatal|error|remote):' })      # the real error, not git's generic advice lines
    if ($f.Count) { return ($f[0] -replace '^(fatal|error|remote):\s*', '') }
    if ($l.Count) { return $l[-1] } else { return '' }
}
try {
    if (Test-Path -LiteralPath (Join-Path $dest '.git')) {
        if (-not $update) { $res.Status = 'present'; return [pscustomobject]$res }
        $dirty = & git -C $dest status --porcelain 2>&1
        if ($dirty) { $res.Status = 'skipped'; $res.Detail = 'has local changes'; return [pscustomobject]$res }
        $shallow = (& git -C $dest rev-parse --is-shallow-repository 2>$null) -eq 'true'
        if ($shallow) { $o = & git -C $dest fetch --quiet --depth 1 origin 2>&1; if ($LASTEXITCODE -eq 0) { $o = & git -C $dest reset --quiet --hard FETCH_HEAD 2>&1 } }
        else { $o = & git -C $dest pull --quiet --ff-only 2>&1 }
        if ($LASTEXITCODE -ne 0) { $res.Status = 'failed'; $res.Detail = Last-Line $o } else { $res.Status = 'updated' }
        return [pscustomobject]$res
    }
    if (Test-Path -LiteralPath $dest) { $res.Status = 'skipped'; $res.Detail = 'folder exists and is not a git repo'; return [pscustomobject]$res }
    $gitArgs = @('-c', 'core.longpaths=true', 'clone', '--quiet', '--no-tags', '--single-branch')
    if ($depth -gt 0) { $gitArgs += @('--depth', "$depth") }
    $gitArgs += @($r.CloneUrl, $dest)
    for ($try = 1; $try -le 3; $try++) {
        $o = & git @gitArgs 2>&1
        if ($LASTEXITCODE -eq 0) { $res.Status = 'cloned'; return [pscustomobject]$res }
        if (Test-Path -LiteralPath $dest) { Remove-Item -Recurse -Force -LiteralPath $dest -ErrorAction SilentlyContinue }
        if ("$o" -match 'not found|Authentication failed|could not read Username|Repository not found|403') { break }   # no point retrying
        Start-Sleep -Seconds (3 * $try)
    }
    $res.Status = 'failed'; $res.Detail = Last-Line $o
} catch { $res.Status = 'failed'; $res.Detail = $_.Exception.Message }
[pscustomobject]$res
'@

# ---- auth for git: environment only (not persisted, not on the command line)
$saved = @{}
foreach ($n in 'GIT_TERMINAL_PROMPT', 'GIT_LFS_SKIP_SMUDGE', 'GIT_CONFIG_COUNT', 'GIT_CONFIG_KEY_0', 'GIT_CONFIG_VALUE_0') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
$env:GIT_TERMINAL_PROMPT = '0'; $env:GIT_LFS_SKIP_SMUDGE = '1'
if ($Token) {
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("x-access-token:$Token"))
    $env:GIT_CONFIG_COUNT = '1'
    $env:GIT_CONFIG_KEY_0 = "http.$($u.Scheme)://$($u.Host)/.extraheader"
    $env:GIT_CONFIG_VALUE_0 = "AUTHORIZATION: basic $b64"
}

$results = New-Object System.Collections.Generic.List[object]
try {
    $done = 0; $total = $repoList.Count
    if ($PSVersionTable.PSVersion.Major -ge 7 -and $Parallel -gt 1) {
        $repoList | ForEach-Object -ThrottleLimit $Parallel -Parallel {
            $w = [scriptblock]::Create($using:workerText)
            & $w $_ $using:TargetPath $using:Depth $using:Update.IsPresent
        } | ForEach-Object {
            $results.Add($_); $done++
            Write-Progress -Activity "Cloning $org" -Status "$done / $total" -PercentComplete (100 * $done / $total)
        }
    } else {
        $w = [scriptblock]::Create($workerText)
        foreach ($r in $repoList) {
            Write-Progress -Activity "Cloning $org" -Status "$($done + 1) / $total  $($r.Name)" -PercentComplete (100 * $done / $total)
            $results.Add((& $w $r $TargetPath $Depth $Update.IsPresent)); $done++
        }
    }
} finally {
    Write-Progress -Activity "Cloning $org" -Completed
    foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
}

# ---- summary (only what matters)
$by = $results | Group-Object Status -AsHashTable -AsString
function Cnt($k) { if ($by -and $by.ContainsKey($k)) { @($by[$k]).Count } else { 0 } }
$line = @("cloned $(Cnt 'cloned')")
if (Cnt 'updated') { $line += "updated $(Cnt 'updated')" }
if (Cnt 'present') { $line += "already present $(Cnt 'present')" }
if (Cnt 'skipped') { $line += "skipped $(Cnt 'skipped')" }
if (Cnt 'failed')  { $line += "FAILED $(Cnt 'failed')" }
Write-Host ("Done: " + ($line -join ', ') + "  ->  $TargetPath") -ForegroundColor $(if (Cnt 'failed') { 'Yellow' } else { 'Green' })
foreach ($x in @($results | Where-Object { $_.Status -in 'failed', 'skipped' } | Sort-Object Name)) { Write-Host ("  {0,-8} {1}  {2}" -f $x.Status, $x.Name, $x.Detail) -ForegroundColor Yellow }
