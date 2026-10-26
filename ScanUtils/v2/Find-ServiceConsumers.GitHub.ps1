<#
.SYNOPSIS
    Find the consumers of a service across a whole GitHub org/user WITHOUT cloning the repos.

.DESCRIPTION
    Same idea as Find-ServiceConsumers.ps1, but the repos stay on GitHub:

      1. Lists the org's repos (1-2 API calls).
      2. Finds the repo(s) that DEFINE/HOST the service you named (GitHub code search: filename / interface / class)
         and downloads just those repos as source tarballs (no git history) - usually 1-5 repos.
      3. Runs the local discovery on them to learn the contract (interfaces, client classes, .svc names, REST routes).
      4. Code-searches the whole org for those terms and downloads ONLY the matching files (+ the repo's *.csproj files
         so project/package references can be resolved) into a sparse local mirror.
      5. Runs the normal local pipeline (Get-EndpointInventory -> Find-EndpointUsage) on that mirror and writes the
         usual reports. Every download is logged.

    Requirements: a GitHub token that can read the org's repos (classic PAT with 'repo', or fine-grained with
    Contents:read + Metadata:read; SSO-enabled orgs need the token authorised for SSO). Code search REQUIRES auth.
    Pass -Token or set $env:GITHUB_TOKEN. The token is only sent to the API host and is never written to disk/logs.
    `tar` must be on PATH (built into Windows 10+/Linux/macOS).

    GitHub code search limits (docs.github.com): default branch only, files < 384 KB, max 1000 results per query,
    10 code-search requests/minute. The script throttles itself (-SearchDelaySec) and warns when a term hit the
    1000-result cap. Forks are not searched by GitHub unless they are more popular than their parent.
    The search index can lag behind very recent commits.

.EXAMPLE
    $env:GITHUB_TOKEN = '<token>'
    .\Find-ServiceConsumers.GitHub.ps1 -GitHubUrl https://github.com/contoso -Service OrderService.svc
.EXAMPLE
    # blob URLs pin the repo; lists and a list file work as before; GitHub Enterprise Server is supported
    .\Find-ServiceConsumers.GitHub.ps1 -GitHubUrl https://git.contoso.com/platform `
        -Service https://git.contoso.com/platform/OrderWCFServices/blob/main/OrderService.svc,UserController.cs `
        -HostMapPath .\hosts.csv
#>
[CmdletBinding()]
param(
    # Org or user URL, e.g. https://github.com/contoso   (GHE: https://host/org)
    [Parameter(Mandatory)][string]$GitHubUrl,
    # Names, file names, or GitHub blob URLs (comma separated list OK)
    [string[]]$Service = @(),
    [string]$ServiceList,
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$WorkPath = (Join-Path (Get-Location) 'remote-scan'),
    # Override API base (default: https://api.github.com, or https://<host>/api/v3 for GitHub Enterprise Server)
    [string]$ApiBase,
    [string]$HostMapPath,
    [string[]]$ExcludeRepo = @(),
    [switch]$SkipArchived,
    # Seconds between code-search calls (GitHub allows 10/min)
    [int]$SearchDelaySec = 7,
    # Safety caps on what gets downloaded in full / per consumer repo
    [int]$MaxFullRepos = 8,
    [int]$MaxFullRepoSizeMB = 300,
    [int]$MaxFilesPerRepo = 150,
    # Extra code-search terms to look for (e.g. a legacy host name)
    [string[]]$ExtraSearchTerms = @(),
    [switch]$DiscoverOnly,
    [switch]$Refresh,
    [switch]$IncludeSelf,
    [switch]$IncludeTests
)

$ErrorActionPreference = 'Stop'
if (-not $Token) { throw "No GitHub token. Pass -Token or set `$env:GITHUB_TOKEN (needs read access to the org's repos; code search requires authentication)." }
if (-not (Get-Command tar -ErrorAction SilentlyContinue)) { throw "'tar' not found on PATH (needed to unpack repo tarballs)." }

function Split-List($s) { if ([string]::IsNullOrWhiteSpace($s)) { return @() }; return @($s -split ';' | Where-Object { $_ }) }

# ------------------------------------------------------------------------------------------------
# URL / API setup
# ------------------------------------------------------------------------------------------------
$gu = [uri]$GitHubUrl
$org = ($gu.AbsolutePath.Trim('/') -split '/')[0]
if (-not $org) { throw "Could not read an org/user name from '$GitHubUrl' (expected e.g. https://github.com/contoso)." }
$ghHost = $gu.Host
if ($ApiBase) { $ApiBaseUrl = $ApiBase } elseif ($ghHost -eq 'github.com') { $ApiBaseUrl = 'https://api.github.com' } else { $ApiBaseUrl = "$($gu.Scheme)://$ghHost/api/v3" }
$ApiBaseUrl = $ApiBaseUrl.TrimEnd('/')
$ApiHost = ([uri]$ApiBaseUrl).Host

$mirror = Join-Path $WorkPath 'mirror'
if ($Refresh -and (Test-Path $mirror)) { Remove-Item -Recurse -Force $mirror }
New-Item -ItemType Directory -Force -Path $mirror | Out-Null
$fetchLog = New-Object System.Collections.Generic.List[object]
$script:lastSearch = [datetime]::MinValue
$script:searchCalls = 0

function Get-HdrVal($h, [string]$n) {
    if (-not $h) { return $null }
    try { $v = $h.GetValues($n); if ($v) { return @($v)[0] } } catch { }
    try { return $h[$n] } catch { }
    return $null
}

function Invoke-Gh {
    param([string]$Path, [hashtable]$Query, [string]$Accept = 'application/vnd.github+json', [string]$OutFile, [switch]$Search)
    $url = if ($Path -match '^https?://') { $Path } else { $ApiBaseUrl + $Path }
    if ($Query -and $Query.Count) {
        $qs = (($Query.GetEnumerator() | ForEach-Object { [uri]::EscapeDataString($_.Key) + '=' + [uri]::EscapeDataString([string]$_.Value) }) -join '&')
        $url += $(if ($url.Contains('?')) { '&' } else { '?' }) + $qs
    }
    $headers = @{ Authorization = "Bearer $Token"; Accept = $Accept; 'User-Agent' = 'endpoint-scan'; 'X-GitHub-Api-Version' = '2022-11-28' }
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        if ($Search) {
            $wait = $SearchDelaySec - ((Get-Date) - $script:lastSearch).TotalSeconds
            if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
            $script:lastSearch = Get-Date; $script:searchCalls++
        }
        try {
            $p = @{ Uri = $url; Headers = $headers; Method = 'GET'; UseBasicParsing = $true; ErrorAction = 'Stop' }
            if ($OutFile) { $p.OutFile = $OutFile; $p.PassThru = $true }
            return (Invoke-WebRequest @p)
        } catch {
            $code = 0; $hdr = $null
            if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode; $hdr = $_.Exception.Response.Headers }
            if ($code -eq 404) { return $null }
            if ($code -eq 401) { throw "GitHub returned 401 Unauthorized - check the token (and SSO authorisation for the org)." }
            if ($code -in 403, 429) {
                $retry = Get-HdrVal $hdr 'Retry-After'; $remain = Get-HdrVal $hdr 'X-RateLimit-Remaining'; $reset = Get-HdrVal $hdr 'X-RateLimit-Reset'
                $sleep = 60
                if ($retry) { $sleep = [int]$retry + 1 }
                elseif ($remain -eq '0' -and $reset) { $sleep = [Math]::Max(5, [int]([DateTimeOffset]::FromUnixTimeSeconds([long]$reset).ToLocalTime() - [DateTimeOffset]::Now).TotalSeconds + 2) }
                if ($sleep -gt 900 -or $attempt -ge 5) { throw "GitHub rate limit / permission problem (HTTP $code) on $($url -replace '\?.*$','') - $($_.Exception.Message)" }
                Write-Host "  rate-limited (HTTP $code), waiting $sleep s ..." -ForegroundColor Yellow
                Start-Sleep -Seconds $sleep; continue
            }
            if ($code -ge 500 -and $attempt -lt 5) { Start-Sleep -Seconds (2 * $attempt); continue }
            throw
        }
    }
}

function ConvertFrom-Resp($resp) { if ($null -eq $resp) { return $null }; return ($resp.Content | ConvertFrom-Json) }

# ------------------------------------------------------------------------------------------------
# 1. repo list
# ------------------------------------------------------------------------------------------------
Write-Host "Step 1/6  Listing repos of '$org' on $ghHost ..." -ForegroundColor Cyan
$scopeQ = "org:$org"
$repoMeta = @{}
$kind = 'orgs'
if ($null -eq (Invoke-Gh "/orgs/$org")) { $kind = 'users'; $scopeQ = "user:$org" }
$page = 1
while ($true) {
    $r = ConvertFrom-Resp (Invoke-Gh "/$kind/$org/repos" @{ per_page = 100; page = $page; type = 'all' })
    if ($null -eq $r) { throw "Org/user '$org' not found or not visible to this token." }
    $items = @($r); if ($items.Count -eq 0) { break }
    foreach ($x in $items) {
        if ($ExcludeRepo -contains $x.name) { continue }
        if ($SkipArchived -and $x.archived) { continue }
        $repoMeta[$x.name] = [pscustomobject]@{ Name = $x.name; Branch = $x.default_branch; SizeMB = [math]::Round($x.size / 1024.0, 1); Archived = [bool]$x.archived; Fork = [bool]$x.fork }
    }
    if ($items.Count -lt 100) { break }; $page++
}
Write-Host "          $($repoMeta.Count) repos visible ($(@($repoMeta.Values | Where-Object Archived).Count) archived, $(@($repoMeta.Values | Where-Object Fork).Count) forks - GitHub code search skips most forks)"

# ------------------------------------------------------------------------------------------------
# helpers: search, download
# ------------------------------------------------------------------------------------------------
$truncatedTerms = New-Object System.Collections.Generic.List[string]
$skipPath = '(^|/)(bin|obj|node_modules|packages|\.git|\.vs|wwwroot/lib)/'
function Search-Code([string]$q) {
    $res = New-Object System.Collections.Generic.List[object]
    $pg = 1; $total = 0
    while ($pg -le 10) {
        $r = ConvertFrom-Resp (Invoke-Gh '/search/code' @{ q = $q; per_page = 100; page = $pg } -Search)
        if ($null -eq $r) { break }
        $total = [int]$r.total_count
        $items = @($r.items); if ($items.Count -eq 0) { break }
        foreach ($i in $items) {
            if (-not $repoMeta.ContainsKey($i.repository.name)) { continue }
            if ($i.path -match $skipPath) { continue }
            $res.Add([pscustomobject]@{ Repo = $i.repository.name; Path = $i.path; Name = $i.name })
        }
        if ($items.Count -lt 100 -or ($pg * 100) -ge $total) { break }; $pg++
    }
    if ($total -gt 1000) { $truncatedTerms.Add("$q  (total $total, only first 1000 returned)") }
    return $res.ToArray()
}

function Get-SafeLocal([string]$repo, [string]$path) {
    if ($path -match '(^|[\\/])\.\.([\\/]|$)') { throw "Refusing suspicious path '$path'" }
    $full = Join-Path (Join-Path $mirror $repo) ($path -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    return $full
}

function Save-File([string]$repo, [string]$path, [string]$why) {
    $dest = Get-SafeLocal $repo $path
    if (Test-Path -LiteralPath $dest) { return $false }
    New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($dest)) | Out-Null
    $enc = (($path -split '/') | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
    $r = Invoke-Gh "/repos/$org/$repo/contents/$enc" -Accept 'application/vnd.github.raw' -OutFile $dest
    if ($null -eq $r) { return $false }
    $fetchLog.Add([pscustomobject]@{ Repo = $repo; Path = $path; Why = $why; Mode = 'file' })
    return $true
}

$fullRepos = New-Object 'System.Collections.Generic.HashSet[string]'
function Add-FullRepo([string]$repo, [string]$why) {
    if ($fullRepos.Contains($repo)) { return $true }
    if (-not $repoMeta.ContainsKey($repo)) { Write-Warning "Repo '$repo' is not visible to the token - skipped."; return $false }
    $m = $repoMeta[$repo]
    if ($fullRepos.Count -ge $MaxFullRepos) { Write-Warning "Not downloading '$repo' in full: -MaxFullRepos $MaxFullRepos reached ($why)."; return $false }
    if ($m.SizeMB -gt $MaxFullRepoSizeMB) { Write-Warning "Not downloading '$repo' in full: $($m.SizeMB) MB > -MaxFullRepoSizeMB $MaxFullRepoSizeMB."; return $false }
    $dest = Join-Path $mirror $repo
    $tmp = Join-Path $WorkPath ("tmp-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        $tgz = Join-Path $tmp 'repo.tar.gz'
        $r = Invoke-Gh "/repos/$org/$repo/tarball" -OutFile $tgz
        if ($null -eq $r) { Write-Warning "Tarball for '$repo' not available."; return $false }
        & tar -xzf $tgz -C $tmp --exclude=node_modules --exclude=bin --exclude=obj 2>&1 | Out-Null
        $top = Get-ChildItem -LiteralPath $tmp -Directory | Select-Object -First 1
        if (-not $top) { Write-Warning "Could not unpack '$repo'."; return $false }
        if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
        Move-Item -LiteralPath $top.FullName -Destination $dest
    } finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
    [void]$fullRepos.Add($repo)
    $fetchLog.Add([pscustomobject]@{ Repo = $repo; Path = '(entire repo, default branch)'; Why = $why; Mode = 'tarball' })
    Write-Host ("          downloaded in full: {0} ({1} MB)  - {2}" -f $repo, $m.SizeMB, $why)
    return $true
}

# ------------------------------------------------------------------------------------------------
# input tokens (names / file names / blob URLs)
# ------------------------------------------------------------------------------------------------
$raw = @($Service)
if ($ServiceList) { $raw += @(Get-Content -LiteralPath $ServiceList | Where-Object { $_.Trim() -and $_.Trim() -notmatch '^#' }) }
$tokens = @()
foreach ($r0 in $raw) {
    foreach ($t in ($r0 -split '[,;\r\n]')) {
        $t = $t.Trim().Trim('"', "'"); if (-not $t) { continue }
        $pinRepo = ''; $path = $t
        if ($t -match '^https?://[^/]+/([^/]+)/([^/]+)/(?:blob|tree)/[^/]+/(.+)$') {          # https://host/org/repo/blob/branch/path/File.svc
            $pinRepo = $Matches[2]; $path = $Matches[3] -replace '[?#].*$', ''
        }
        $leaf = ($path -split '[\\/]')[-1]
        $key = ($leaf -replace '(?i)\.svc\.cs$', '' -replace '(?i)\.(svc|cs)$', '')
        $k = if ($leaf -match '(?i)\.svc$') { 'svc' } elseif ($leaf -match '(?i)\.cs$') { 'file' } else { 'any' }
        $tokens += [pscustomobject]@{ Raw = $t; Leaf = $leaf; Key = $key; Kind = $k; PinRepo = $pinRepo; Local = $(if ($pinRepo) { "$pinRepo/$path" } else { $leaf }) }
    }
}
if ($tokens.Count -eq 0) { throw 'Nothing to search for: pass -Service and/or -ServiceList.' }

# ------------------------------------------------------------------------------------------------
# 2. find + download the definition/host repos
# ------------------------------------------------------------------------------------------------
Write-Host "Step 2/6  Locating the repo(s) that define/host: $(($tokens | ForEach-Object { $_.Leaf }) -join ', ')" -ForegroundColor Cyan
$defHits = New-Object System.Collections.Generic.List[object]
foreach ($t in $tokens) {
    if ($t.PinRepo) { $defHits.Add([pscustomobject]@{ Repo = $t.PinRepo; Why = "pinned by URL ($($t.Leaf))" }); continue }
    $queries = switch ($t.Kind) {
        'svc'  { @("filename:$($t.Leaf)") }
        'file' { @("filename:$($t.Leaf)") }
        default { @("filename:$($t.Key).svc", "filename:$($t.Key).cs", "`"interface $($t.Key)`" extension:cs", "`"class $($t.Key)`" extension:cs") }
    }
    foreach ($q in $queries) {
        foreach ($h in (Search-Code "$q $scopeQ")) {
            if ($q -like 'filename:*' -and $h.Name -ine ($q -replace '^filename:', '')) { continue }
            $defHits.Add([pscustomobject]@{ Repo = $h.Repo; Why = "$q -> $($h.Path)" })
        }
    }
}
$defRepos = @($defHits | Group-Object Repo)
if ($defRepos.Count -eq 0) { Write-Host "Nothing found for the given name(s) in '$org'. Check spelling, token access (SSO), or that the file is on the default branch." -ForegroundColor Yellow; return }
foreach ($g in $defRepos) { [void](Add-FullRepo $g.Name ($g.Group[0].Why)) }
if ($fullRepos.Count -eq 0) { Write-Host 'No repo could be downloaded in full (see warnings).' -ForegroundColor Yellow; return }

# ------------------------------------------------------------------------------------------------
# 3. local discovery on the mirror (+ one hop to fetch shared contract libraries)
# ------------------------------------------------------------------------------------------------
$localTokens = @($tokens | ForEach-Object { $_.Local })
$disc = Join-Path $WorkPath 'discover'
function Invoke-LocalDiscovery {
    if (Test-Path $disc) { Remove-Item -Recurse -Force $disc }
    $p = @{ RootPath = $mirror; Service = $localTokens; WorkPath = $disc; DiscoverOnly = $true; SkipGit = $true; Rescan = $true }
    & (Join-Path $PSScriptRoot 'Find-ServiceConsumers.ps1') @p | Out-Host
    $f = Join-Path $disc 'discovered-endpoints.csv'
    if (Test-Path $f) { return @(Import-Csv $f) } else { return @() }
}
Write-Host "Step 3/6  Discovering the contract(s) from the downloaded repo(s) ..." -ForegroundColor Cyan
$found = Invoke-LocalDiscovery
if ($found.Count -eq 0) { Write-Host 'The downloaded repo(s) do not contain a matching endpoint (see messages above).' -ForegroundColor Yellow; return }

$clusterRows = @(Import-Csv (Join-Path $disc 'inventory\endpoint-clusters.csv') | Where-Object { $found.ClusterId -contains $_.ClusterId })
$hop = @()
foreach ($c in ($clusterRows | Where-Object Kind -eq 'WCF')) {
    foreach ($n in (Split-List $c.InterfaceNames)) {
        foreach ($h in (Search-Code "`"interface $n`" extension:cs $scopeQ")) { if (-not $fullRepos.Contains($h.Repo)) { $hop += $h.Repo } }
    }
}
$hop = @($hop | Select-Object -Unique)
if ($hop.Count -gt 0) {
    Write-Host "          other repos declare the same interface name(s): $($hop -join ', ') - fetching them to resolve duplicates/shared contracts"
    $added = 0; foreach ($r2 in $hop) { if (Add-FullRepo $r2 'declares a matching interface name') { $added++ } }
    if ($added -gt 0) { $found = Invoke-LocalDiscovery; $clusterRows = @(Import-Csv (Join-Path $disc 'inventory\endpoint-clusters.csv') | Where-Object { $found.ClusterId -contains $_.ClusterId }) }
}
if ($DiscoverOnly) {
    $fetchLog | Export-Csv -Path (Join-Path $WorkPath 'fetch-log.csv') -NoTypeInformation -Encoding UTF8
    Write-Host "Discovery only. Mirror: $mirror" ; return
}

# ------------------------------------------------------------------------------------------------
# 4. search terms from the discovered contract(s)
# ------------------------------------------------------------------------------------------------
$terms = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($c in $clusterRows) {
    if ($c.Kind -eq 'WCF') {
        foreach ($x in (Split-List $c.InterfaceNames)) { [void]$terms.Add($x) }
        foreach ($x in (Split-List $c.ClientClasses))  { [void]$terms.Add($x) }
        foreach ($x in (Split-List $c.SvcFiles))       { $l = ($x -split '[\\/]')[-1]; if ($l -match '\.svc$') { [void]$terms.Add($l) } }
    } else {
        $segs = @($c.RouteNormalized -split '/' | Where-Object { $_ })
        $stat = @(); foreach ($sg in $segs) { if ($sg.Contains('{')) { break }; $stat += $sg }
        if ($stat.Count -ge 2) { [void]$terms.Add(($stat[0..1] -join '/')) } elseif ($stat.Count -eq 1 -and $stat[0].Length -ge 4) { [void]$terms.Add($stat[0]) }
    }
}
if ($HostMapPath) {
    $owners = @($clusterRows | ForEach-Object { @(Split-List $_.HostRepos) + @(Split-List $_.DefinitionRepos) + @($_.Owner) } | Select-Object -Unique)
    foreach ($r in (Import-Csv $HostMapPath)) { if ($owners -contains $r.Repo) { foreach ($h in ($r.Host -split ';')) { $h = ($h.Trim() -replace '^[a-z]+://', '' -replace '/.*$', ''); if ($h) { [void]$terms.Add($h) } } } }
}
foreach ($x in $ExtraSearchTerms) { if ($x) { [void]$terms.Add($x) } }
$termList = @($terms)
Write-Host "Step 4/6  Searching '$org' for $($termList.Count) term(s): $($termList -join ', ')" -ForegroundColor Cyan
Write-Host ("          (about {0} code-search calls at {1}s spacing = ~{2} min)" -f $termList.Count, $SearchDelaySec, [math]::Ceiling($termList.Count * $SearchDelaySec / 60.0))

$cand = @{}   # repo -> ordered paths
foreach ($term in $termList) {
    $hits = @(Search-Code "`"$term`" $scopeQ")
    Write-Host ("          {0,-40} {1} file(s)" -f $term, $hits.Count)
    foreach ($h in $hits) {
        if ($fullRepos.Contains($h.Repo)) { continue }
        if (-not $cand.ContainsKey($h.Repo)) { $cand[$h.Repo] = New-Object System.Collections.Generic.List[string] }
        if (-not $cand[$h.Repo].Contains($h.Path) -and $cand[$h.Repo].Count -lt $MaxFilesPerRepo) { $cand[$h.Repo].Add($h.Path) }
    }
}
Write-Host "          candidate consumer repos: $($cand.Count)"

# ------------------------------------------------------------------------------------------------
# 5. sparse download of matching files (+ csproj for reference resolution)
# ------------------------------------------------------------------------------------------------
Write-Host "Step 5/6  Downloading matching files only ..." -ForegroundColor Cyan
$n = 0
foreach ($repo in ($cand.Keys | Sort-Object)) {
    $n++
    Write-Progress -Activity 'Downloading matching files' -Status "$repo ($n/$($cand.Count))" -PercentComplete (100 * $n / [Math]::Max(1, $cand.Count))
    foreach ($p in $cand[$repo]) { [void](Save-File $repo $p 'code-search hit') }
    $tree = ConvertFrom-Resp (Invoke-Gh "/repos/$org/$repo/git/trees/$($repoMeta[$repo].Branch)" @{ recursive = 1 })
    if ($tree) {
        $proj = @($tree.tree | Where-Object { $_.type -eq 'blob' -and $_.path -match '\.(cs|vb|fs)proj$' -and $_.path -notmatch $skipPath -and [int]$_.size -lt 500KB } | Select-Object -First 25)
        foreach ($pr in $proj) { [void](Save-File $repo $pr.path 'project file (reference resolution)') }
        if ($tree.truncated) { Write-Warning "File tree of '$repo' was truncated by GitHub; some project files may be missing." }
    }
}
Write-Progress -Activity 'Downloading matching files' -Completed
$fetchLog | Export-Csv -Path (Join-Path $WorkPath 'fetch-log.csv') -NoTypeInformation -Encoding UTF8
Write-Host ("          {0} repo(s) in full, {1} sparse repo(s), {2} file(s) downloaded  (log: fetch-log.csv)" -f $fullRepos.Count, $cand.Count, @($fetchLog | Where-Object Mode -eq 'file').Count)

# ------------------------------------------------------------------------------------------------
# 6. normal local analysis on the mirror
# ------------------------------------------------------------------------------------------------
Write-Host "Step 6/6  Analysing the mirror ..." -ForegroundColor Cyan
$p = @{ RootPath = $mirror; Service = $localTokens; WorkPath = (Join-Path $WorkPath 'final'); SkipGit = $true; Rescan = $true }
if ($HostMapPath) { $p.HostMapPath = $HostMapPath }
if ($IncludeSelf) { $p.IncludeSelf = $true }
if ($IncludeTests) { $p.IncludeTests = $true }
& (Join-Path $PSScriptRoot 'Find-ServiceConsumers.ps1') @p | Out-Host

if ($truncatedTerms.Count -gt 0) {
    Write-Host ''
    Write-Host 'WARNING: these search terms exceeded GitHub''s 1000-result cap - consumers may be missing. Use a more specific term or -ExtraSearchTerms:' -ForegroundColor Yellow
    $truncatedTerms | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
}
Write-Host ''
Write-Host ("Done. {0} code-search calls. Results: {1}" -f $script:searchCalls, (Join-Path $WorkPath 'final\usage')) -ForegroundColor Green
Write-Host 'Reminder: GitHub code search covers default branches only, files < 384 KB, and skips most forks - confirm "no consumer" results with runtime logs.'
