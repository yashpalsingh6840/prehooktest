<#
.SYNOPSIS
    Who uses these services?  Give a list of SOAP services / REST controllers, get a clean report.

.DESCRIPTION
    Input : a folder with your cloned repos (see Clone-GitHubRepos.ps1) and a list of services, e.g.
              OrderService.svc, InventoryService.svc, EmployeeController.cs, OrderController.cs
            (names, file names or full paths; comma separated and/or -ServiceList file)

    -HomeRepoPath : the repo that hosts the services. Metadata (routes, operations) is taken from it only, same-named services in other
                    repos are ignored, and usage inside it (its own frontend) is reported too. -RootPath then defaults to its parent folder.

    Output: exactly two files in -OutputPath (default: current folder) - nothing else is written there:

      service-usage.csv    one row per  service x consuming repo
                           Service | Type | HostedBy | Consumer | Confidence | EndpointsUsed | Evidence | Example
      service-summary.csv  one row per service
                           Service | Type | HostedBy | Contract | Endpoints | Consumers | ConsumerRepos |
                           EndpointsNotSeenUsed | Notes
                           (+ one row per input that matched nothing)

    Usage inside the home repo (e.g. its own frontend) is included and labelled '<repo> (home)'; -ExcludeHome leaves it out.
    Pin an ambiguous name to its home repo with  OrderController.cs@Orders.Api  (or -HomeRepo).

    Only High and Medium confidence consumers are reported. Weak candidates (interface-name-only matches, prefix-only
    URL matches, repos that merely reference a contract package) are hidden and counted in Notes; add -IncludeLow to see them.
    -Detail additionally writes service-usage-evidence.csv (file, line and code snippet for every reported consumer).

    Working files (repo inventory, raw evidence) live in a cache OUTSIDE your repo folder and OUTSIDE the output folder
    (%LOCALAPPDATA%\endpoint-scan on Windows). The cache is reused between runs and refreshed automatically when any repo's
    HEAD changed (e.g. after a pull). -Rescan forces a refresh, -NoCache deletes it after the run.

.EXAMPLE
    # services of ONE home repo: name them ...
    .\Find-ServiceUsage.ps1 -HomeRepoPath C:\Proj\Orders.Api -Service OrderController.cs,EmployeeController.cs,OrderService.svc
.EXAMPLE
    # ... or leave -Service out to search for EVERY SOAP service and controller in the home repo (RootPath defaults to C:\Proj)
    .\Find-ServiceUsage.ps1 -HomeRepoPath C:\Proj\Orders.Api
.EXAMPLE
    .\Find-ServiceUsage.ps1 -RootPath C:\Proj -Service OrderService.svc,InventoryService.svc,EmployeeController.cs,OrderController.cs
.EXAMPLE
    .\Find-ServiceUsage.ps1 -RootPath C:\Proj -ServiceList .\services.txt -OutputPath C:\reports -HostMapPath .\hosts.csv
#>
[CmdletBinding()]
param(
    # Folder with ALL cloned repos. Optional when -HomeRepoPath is given (then: the parent folder of the home repo)
    [string]$RootPath,
    # The repo that hosts the services you are asking about (full path). All -Service entries are taken from this repo; leave -Service out to take every service in it
    [string]$HomeRepoPath,
    [string[]]$Service = @(),
    [string]$ServiceList,
    [string]$OutputPath = (Get-Location).Path,
    # Optional CSV (Repo,Host): lets host names confirm REST/SOAP matches and separates identical routes in different services
    [string]$HostMapPath,
    # Advanced: narrow ambiguous names by repo folder name(s); per service you can also write  OrderController.cs@Orders.Api
    [string[]]$InRepo = @(),
    [string[]]$ExcludeRepo = @(),
    [switch]$IncludeLow,
    # By default usage INSIDE the home repo (e.g. its own frontend) is reported too; this switch leaves it out
    [switch]$ExcludeHome,
    [switch]$Detail,
    [switch]$Rescan,
    [switch]$NoCache,
    [switch]$IncludeTests,
    [string]$CacheRoot
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EndpointScan.Common.ps1')
function Split-List($s) { if ([string]::IsNullOrWhiteSpace($s)) { return @() }; return @($s -split ';' | Where-Object { $_ }) }

# ---- inputs
$rawItems = @($Service)
if ($ServiceList) {
    if (-not (Test-Path -LiteralPath $ServiceList)) { throw "ServiceList file not found: $ServiceList" }
    $rawItems += @(Get-Content -LiteralPath $ServiceList | Where-Object { $_.Trim() -and $_.Trim() -notmatch '^#' })
}
$inputs = @()
foreach ($r in $rawItems) { foreach ($t in ($r -split '[,;\r\n]')) { $t = $t.Trim().Trim('"', "'"); if ($t) { $inputs += $t } } }
$inputs = @($inputs | ForEach-Object { if ($_ -match '^([^@/\\:]+)@([^@/\\]+)$') { "$($Matches[2])/$($Matches[1])" } else { $_ } } | Select-Object -Unique)   # OrderController.cs@Orders.Api -> Orders.Api/OrderController.cs
if ($inputs.Count -eq 0 -and -not $HomeRepoPath) { throw 'Nothing to search for: pass -Service (e.g. OrderService.svc,EmployeeController.cs), -ServiceList, or -HomeRepoPath to search for every service of one repo.' }

# ---- home repo / root folder
$sepChar = [System.IO.Path]::DirectorySeparatorChar
$homeFull = ''
if ($HomeRepoPath) {
    if (-not (Test-Path -LiteralPath $HomeRepoPath -PathType Container)) { throw "HomeRepoPath not found (or not a folder): $HomeRepoPath" }
    $homeFull = (Resolve-Path -LiteralPath $HomeRepoPath).Path.TrimEnd('\', '/')
    if (-not $RootPath) { $RootPath = Split-Path $homeFull -Parent }
}
if (-not $RootPath) { throw 'Pass -RootPath (folder with the cloned repos) or -HomeRepoPath.' }
if (-not (Test-Path -LiteralPath $RootPath)) { throw "RootPath not found: $RootPath" }
$rootFull = (Resolve-Path -LiteralPath $RootPath).Path.TrimEnd('\', '/')
if ($homeFull -and -not ($homeFull -eq $rootFull -or $homeFull.StartsWith($rootFull + $sepChar, [StringComparison]::OrdinalIgnoreCase))) {
    throw "HomeRepoPath '$homeFull' is not inside RootPath '$rootFull' - the home repo must be one of the cloned repos."
}
# pin every service name to the home repo (a path in front of the name tells the engine which repo it lives in)
if ($homeFull) { $inputs = @($inputs | ForEach-Object { if ($_ -match '[\\/]') { $_ } else { $homeFull + '/' + $_ } }) }
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
$OutputPath = (Resolve-Path -LiteralPath $OutputPath).Path

# ---- cache location (never inside the repo folder or the output folder)
if (-not $CacheRoot) {
    if ($env:LOCALAPPDATA) { $CacheRoot = Join-Path $env:LOCALAPPDATA 'endpoint-scan' }
    elseif ($env:XDG_CACHE_HOME) { $CacheRoot = Join-Path $env:XDG_CACHE_HOME 'endpoint-scan' }
    else { $CacheRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.cache/endpoint-scan' }
}
$cache = Join-Path $CacheRoot (Get-ShortHash $rootFull.ToLowerInvariant())
New-Item -ItemType Directory -Force -Path $cache | Out-Null

# ---- is the cached repo scan still valid?  (fingerprint = every repo's HEAD commit)
$excludeDir = @('bin', 'obj', 'node_modules', '.git', '.vs', '.idea', 'packages', 'TestResults', 'artifacts')
$repoDirs = @(& { Find-Repos -Root $rootFull -ExcludeDir $excludeDir -ExcludeRepo $ExcludeRepo } 6>$null)
$hasGit = [bool](Get-Command git -ErrorAction SilentlyContinue)
$fpLines = foreach ($d in $repoDirs) {
    $sha = ''
    if ($hasGit -and (Test-Path -LiteralPath (Join-Path $d '.git'))) { $sha = (& git -C $d rev-parse HEAD 2>$null) }
    if (-not $sha) { $sha = (Get-Item -LiteralPath $d).LastWriteTimeUtc.Ticks }
    (Split-Path $d -Leaf) + '=' + $sha
}
$fingerprint = Get-ShortHash (($fpLines -join ';') + "|tests=$IncludeTests|excl=$($ExcludeRepo -join ',')")
$fpFile = Join-Path $cache 'fingerprint.txt'
$cachedFp = if (Test-Path $fpFile) { (Get-Content $fpFile -Raw).Trim() } else { '' }
$needScan = $Rescan -or ($cachedFp -ne $fingerprint) -or -not (Test-Path (Join-Path $cache 'inventory\endpoint-clusters.csv'))
if ($needScan) { Write-Host ("Scanning {0} repos in {1} ..." -f $repoDirs.Count, $rootFull) }
else { Write-Host ("Using cached scan of {0} repos (unchanged since last run; -Rescan to force)." -f $repoDirs.Count) }

# ---- no -Service given: take every SOAP service and controller of the home repo
$homeLeaf = if ($homeFull) { Split-Path $homeFull -Leaf } else { '' }
if ($homeFull) {   # a sub-folder of a repo means that repo
    $owning = @($repoDirs | Where-Object { $homeFull -eq $_ -or $homeFull.StartsWith($_ + $sepChar, [StringComparison]::OrdinalIgnoreCase) } | Sort-Object { $_.Length } -Descending | Select-Object -First 1)
    if ($owning.Count -gt 0 -and $owning[0]) { $homeLeaf = Split-Path $owning[0] -Leaf }
}
if ($inputs.Count -eq 0) {
    $invDir = Join-Path $cache 'inventory'
    if ($needScan) {
        $ip = @{ RootPath = $rootFull; OutputPath = $invDir }
        if ($ExcludeRepo.Count) { $ip.ExcludeRepo = $ExcludeRepo }
        if ($IncludeTests) { $ip.IncludeTests = $true }
        $oldProgress = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
        try { & (Join-Path $PSScriptRoot 'Get-EndpointInventory.ps1') @ip 6>$null | Out-Null } finally { $ProgressPreference = $oldProgress }
        Set-Content -LiteralPath $fpFile -Value $fingerprint
        $needScan = $false
    }
    $repoRows = @(Import-Csv (Join-Path $invDir 'repos.csv'))
    $norm = { param($x) (($x -replace '/', '\').TrimEnd('\')).ToLowerInvariant() }
    $homeName = ''; $best = 0
    foreach ($r in $repoRows) { $d = & $norm $r.Dir; $h = & $norm $homeFull; if (($h -eq $d -or $h.StartsWith($d + '\')) -and $d.Length -gt $best) { $homeName = $r.Name; $best = $d.Length } }
    if (-not $homeName) { throw "'$homeFull' is not one of the scanned repos (looked in $rootFull)." }
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($c in (Import-Csv (Join-Path $invDir 'endpoint-clusters.csv'))) {
        $repos4 = @(@(Split-List $c.HostRepos) + @(Split-List $c.ImplRepos) + @(Split-List $c.DefinitionRepos) + @($c.Owner))
        if ($repos4 -notcontains $homeName) { continue }
        if ($c.Kind -eq 'WCF') {
            $t = @(Split-List $c.SvcFiles | ForEach-Object { ($_ -split '[\\/]')[-1] } | Select-Object -First 1)[0]
            if (-not $t) { $t = @(Split-List $c.InterfaceNames | Select-Object -First 1)[0] }
        } else { $t = (@(Split-List $c.Operations | Select-Object -First 1)[0] -split '\.')[0] }
        if ($t -and -not $names.Contains($t)) { $names.Add($t) }
    }
    if ($names.Count -eq 0) { throw "No SOAP services or REST controllers were found in the home repo '$homeLeaf'." }
    $inputs = @($names | ForEach-Object { $homeFull + '/' + $_ })
    Write-Host ("Home repo {0}: {1} service(s) found: {2}" -f $homeLeaf, $names.Count, (($names | Select-Object -First 12) -join ', ') + $(if ($names.Count -gt 12) { ', ...' } else { '' }))
} elseif ($homeFull) { Write-Host "Home repo: $homeLeaf" }

# ---- run the engine quietly (all its chatter and files stay in the cache)
foreach ($n in 'discovered-endpoints.csv', 'input-resolution.csv') { Remove-Item -LiteralPath (Join-Path $cache $n) -Force -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath (Join-Path $cache 'usage') -Recurse -Force -ErrorAction SilentlyContinue
$p = @{ RootPath = $rootFull; Service = $inputs; WorkPath = $cache }
if ($needScan) { $p.Rescan = $true }
if ($HostMapPath) { $p.HostMapPath = $HostMapPath }
if ($InRepo.Count) { $p.InRepo = $InRepo }
if ($ExcludeRepo.Count) { $p.ExcludeRepo = $ExcludeRepo }
if ($IncludeTests) { $p.IncludeTests = $true }
if (-not $ExcludeHome) { $p.IncludeSelf = $true }
$oldProgress = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
try { & (Join-Path $PSScriptRoot 'Find-ServiceConsumers.ps1') @p 6>$null | Out-Null } finally { $ProgressPreference = $oldProgress }
if ($needScan) { Set-Content -LiteralPath $fpFile -Value $fingerprint }

# ---- read engine output
function Import-Opt($path) { if (Test-Path -LiteralPath $path) { return @(Import-Csv -LiteralPath $path) } else { return @() } }
$disc    = Import-Opt (Join-Path $cache 'discovered-endpoints.csv')
$inputRes = Import-Opt (Join-Path $cache 'input-resolution.csv')
$cons    = Import-Opt (Join-Path $cache 'usage\consumer-summary.csv')
$evid    = Import-Opt (Join-Path $cache 'usage\usage-detail.csv')
$clusterById = @{}
foreach ($c in (Import-Opt (Join-Path $cache 'inventory\endpoint-clusters.csv'))) { $clusterById[$c.ClusterId] = $c }

$rankOf = @{ High = 3; Medium = 2; Low = 1 }
$minRank = if ($IncludeLow) { 1 } else { 2 }
$evText = @{
    GeneratedProxy = 'generated WCF proxy'; ManualClientProxy = 'WCF client class'; ConfigClientEndpoint = 'WCF client endpoint in config'
    CodeReference = 'uses the contract/client type'; OperationCall = 'calls operations'; ServiceUrlReference = 'references the .svc URL'
    RouteLiteral = 'calls the URL'; LibraryReference = 'references the contract package'
}

# ---- group discovered clusters into "services" (SOAP: one per contract; REST: one per controller)
$groups = [ordered]@{}
foreach ($d in $disc) {
    $c = $clusterById[$d.ClusterId]; if (-not $c) { continue }
    if ($c.Kind -eq 'WCF') {
        $svc = @(Split-List $c.SvcFiles | ForEach-Object { ($_ -split '[\\/]')[-1] } | Select-Object -First 1)[0]
        if (-not $svc) { $svc = @(Split-List $c.InterfaceNames | Select-Object -First 1)[0] }
        if (-not $svc) { $svc = $c.Name }
        $key = 'W|' + $c.ClusterId; $type = 'SOAP'
    } else {
        $ctl = (@(Split-List $c.Operations | Select-Object -First 1)[0] -split '\.')[0]
        if (-not $ctl) { $ctl = $c.Name }
        $key = 'R|' + $ctl + '|' + $c.Owner; $svc = $ctl; $type = 'REST'
    }
    if (-not $groups.Contains($key)) { $groups[$key] = [pscustomobject]@{ Service = $svc; Type = $type; Clusters = (New-Object System.Collections.Generic.List[object]) } }
    $groups[$key].Clusters.Add($c)
}

$usageRows = New-Object System.Collections.Generic.List[object]
$summaryRows = New-Object System.Collections.Generic.List[object]
$evidenceRows = New-Object System.Collections.Generic.List[object]

foreach ($g in $groups.Values) {
    $cl = $g.Clusters.ToArray()
    $ids = @{}; foreach ($c in $cl) { $ids[$c.ClusterId] = $c }
    $rows = @($cons | Where-Object { $ids.ContainsKey($_.ClusterId) })
    $visible = @($rows | Where-Object { $rankOf[$_.BestConfidence] -ge $minRank })
    $visibleRepos = @($visible | Select-Object -ExpandProperty ConsumerRepo -Unique | Sort-Object)
    $homeVisible = @($visible | Where-Object { $_.InHomeRepo -eq 'True' } | Select-Object -ExpandProperty ConsumerRepo -Unique)
    $hiddenRepos = @($rows | Where-Object { $visibleRepos -notcontains $_.ConsumerRepo } | Select-Object -ExpandProperty ConsumerRepo -Unique)

    # who hosts it
    $owners = @($cl | ForEach-Object { $_.Owner } | Where-Object { $_ } | Select-Object -Unique)
    $multiHost = @($cl | Where-Object { $_.Flags -match 'MultiHost' -or $_.Flags -match 'DuplicateRoute' }).Count -gt 0
    $allHosts = @($cl | ForEach-Object { Split-List $_.HostRepos } | Select-Object -Unique)
    $hostedBy = if ($multiHost -and $allHosts.Count -gt 1) { $allHosts -join ' / ' } else { $owners -join ' / ' }

    # endpoints of this service
    if ($g.Type -eq 'SOAP') { $allEndpoints = @($cl | ForEach-Object { Split-List $_.Operations } | Select-Object -Unique | Sort-Object) }
    else { $allEndpoints = @($cl | ForEach-Object { ($_.HttpVerb + ' /' + (@(Split-List $_.Route)[0])) } | Select-Object -Unique | Sort-Object) }
    $usedEndpoints = New-Object 'System.Collections.Generic.HashSet[string]'

    foreach ($repo in $visibleRepos) {
        $mine = @($visible | Where-Object { $_.ConsumerRepo -eq $repo })
        $isHome = @($mine | Where-Object { $_.InHomeRepo -eq 'True' }).Count -gt 0 -and @($mine | Where-Object { $_.InHomeRepo -ne 'True' }).Count -eq 0
        $best = $mine | Sort-Object { - $rankOf[$_.BestConfidence] } | Select-Object -First 1
        if ($g.Type -eq 'SOAP') {
            $eps = @($mine | ForEach-Object { Split-List $_.OperationsUsed } | Select-Object -Unique | Sort-Object)
        } else {
            $eps = @($mine | ForEach-Object { $c = $ids[$_.ClusterId]; $c.HttpVerb + ' /' + (@(Split-List $c.Route)[0]) } | Select-Object -Unique | Sort-Object)
        }
        foreach ($e in $eps) { [void]$usedEndpoints.Add($e) }
        $types = @($mine | ForEach-Object { $_.EvidenceTypes -split ';' } | Where-Object { $_ } | Select-Object -Unique | ForEach-Object { if ($evText.ContainsKey($_)) { $evText[$_] } else { $_ } })
        $target = @($mine | ForEach-Object { Split-List $_.TargetHostRepos } | Select-Object -Unique)
        $hb = if ($target.Count -gt 0) { $target -join ' / ' } else { $hostedBy }
        $example = if ($best.BestFile) { "$($best.BestFile):$($best.BestLine)" } else { '(project/package reference)' }
        $usageRows.Add([pscustomobject][ordered]@{
            Service = $g.Service; Type = $g.Type; HostedBy = $hb; Consumer = $(if ($isHome) { "$repo (home)" } else { $repo }); Confidence = $best.BestConfidence
            EndpointsUsed = $(if ($eps.Count) { $eps -join '; ' } else { '(not determined)' }); Evidence = ($types -join '; '); Example = $example
        })
        if ($Detail) {
            foreach ($e in @($evid | Where-Object { $ids.ContainsKey($_.ClusterId) -and $_.ConsumerRepo -eq $repo -and $rankOf[$_.Confidence] -ge $minRank })) {
                $evidenceRows.Add([pscustomobject][ordered]@{
                    Service = $g.Service; Consumer = $(if ($isHome) { "$repo (home)" } else { $repo }); Endpoint = $(if ($e.Operation -and $e.Operation -ne '*') { $e.Operation } else { $e.Endpoint })
                    Confidence = $e.Confidence; Evidence = $(if ($evText.ContainsKey($e.EvidenceType)) { $evText[$e.EvidenceType] } else { $e.EvidenceType })
                    File = $e.File; Line = $e.Line; Code = $e.Snippet
                })
            }
        }
    }

    # summary row
    $notSeen = @($allEndpoints | Where-Object { -not $usedEndpoints.Contains($_) })
    $notes = New-Object System.Collections.Generic.List[string]
    $f = ($cl | ForEach-Object { $_.Flags }) -join ';'
    if ($visibleRepos.Count -eq 0) { $notes.Add('No consumer found in the scanned repos - confirm with runtime/gateway logs before retiring') }
    if ($multiHost -and $allHosts.Count -gt 1) { $notes.Add("Hosted in several repos: $($allHosts -join ', ') (callers are tied to a host only if hosts.csv is given)") }
    if ($f -match 'DefinedInMultipleRepos') { $notes.Add('Contract is also declared in: ' + ((@($cl | ForEach-Object { Split-List $_.DefinitionRepos } | Select-Object -Unique) | Where-Object { $owners -notcontains $_ }) -join ', ') + ' (shared contract library or copies)') }
    if ($f -match 'NameCollision') { $notes.Add('Another, different service in these repos uses the same interface name') }
    if ($f -match 'UnresolvedRoute') { $notes.Add('Some routes could not be resolved from the code (route built from a constant/expression that was not found in the same repo) - callers cannot be matched for those endpoints') }
    if ($f -match 'ConventionRoute') { $notes.Add('Some routes come from convention routing rather than an explicit [Route] - verify the URL pattern') }
    if ($f -match 'ContractNotInScannedRepos') { $notes.Add('Contract source not found in scanned repos - operation list may be incomplete') }
    if ($hiddenRepos.Count -gt 0) { $notes.Add("$($hiddenRepos.Count) low-confidence candidate repo(s) hidden: $($hiddenRepos -join ', ') (use -IncludeLow)") }
    $contract = if ($g.Type -eq 'SOAP') { (@($cl | ForEach-Object { Split-List $_.InterfaceNames } | Select-Object -First 1)[0]) } else { '' }
    $summaryRows.Add([pscustomobject][ordered]@{
        Service = $g.Service; Type = $g.Type; HostedBy = $hostedBy; Contract = $contract; Endpoints = $allEndpoints.Count
        Consumers = $visibleRepos.Count; ConsumerRepos = (($visibleRepos | ForEach-Object { if ($homeVisible -contains $_) { "$_ (home)" } else { $_ } }) -join '; ')
        EndpointsNotSeenUsed = $(if ($visibleRepos.Count -gt 0) { $notSeen -join '; ' } else { '' }); Notes = ($notes -join ' | ')
    })
}

# the same service name hosted in several repos: say so on every such row
foreach ($dup in ($summaryRows.ToArray() | Group-Object Service | Where-Object { $_.Count -gt 1 })) {
    foreach ($row in $dup.Group) {
        $others = @($dup.Group | Where-Object { $_ -ne $row } | ForEach-Object { $_.HostedBy } | Select-Object -Unique)
        $msg = "Same name is also hosted by: $($others -join ', ') - reported separately; pick one with Name@Repo (e.g. $($row.Service)@$(($row.HostedBy -split ' / ')[0]))"
        $row.Notes = $(if ($row.Notes) { "$msg | $($row.Notes)" } else { $msg })
    }
}

# inputs that matched nothing
$notFound = @($inputRes | Where-Object { -not $_.MatchedClusters } | Select-Object -ExpandProperty Input)
if ($inputRes.Count -eq 0) { $notFound = @($inputs) }
foreach ($nf in $notFound) {
    $nfName = ($nf -split '[\\/]')[-1]
    $summaryRows.Add([pscustomobject][ordered]@{
        Service = $nfName; Type = ''; HostedBy = ''; Contract = ''; Endpoints = 0; Consumers = 0; ConsumerRepos = ''; EndpointsNotSeenUsed = ''
        Notes = $(if ($homeFull) { "Not found in the home repo ($homeLeaf) - check the spelling / extension (.svc or .cs), or use the interface / controller name without extension" } else { 'Not found in any scanned repo - check the spelling / extension (.svc or .cs), or use the interface / controller name without extension' })
    })
}

# ---- write the report (headers are written even when empty)
function Write-Report($rows, [string]$file, [string[]]$cols) {
    $arr = @($rows.ToArray() | Sort-Object Service, @{ e = { if ($_.PSObject.Properties['Confidence']) { - $rankOf[$_.Confidence] } else { 0 } } }, @{ e = { if ($_.PSObject.Properties['Consumer']) { $_.Consumer } else { '' } } })
    if ($arr.Count -gt 0) { $arr | Export-Csv -LiteralPath $file -NoTypeInformation -Encoding UTF8 }
    else { Set-Content -LiteralPath $file -Value (($cols | ForEach-Object { '"' + $_ + '"' }) -join ',') -Encoding UTF8 }
}
$usageFile = Join-Path $OutputPath 'service-usage.csv'; $sumFile = Join-Path $OutputPath 'service-summary.csv'
Write-Report $usageRows $usageFile @('Service', 'Type', 'HostedBy', 'Consumer', 'Confidence', 'EndpointsUsed', 'Evidence', 'Example')
Write-Report $summaryRows $sumFile @('Service', 'Type', 'HostedBy', 'Contract', 'Endpoints', 'Consumers', 'ConsumerRepos', 'EndpointsNotSeenUsed', 'Notes')
$outFiles = @($usageFile, $sumFile)
if ($Detail) { $evFile = Join-Path $OutputPath 'service-usage-evidence.csv'; Write-Report $evidenceRows $evFile @('Service', 'Consumer', 'Endpoint', 'Confidence', 'Evidence', 'File', 'Line', 'Code'); $outFiles += $evFile }

if ($NoCache) { Remove-Item -LiteralPath $cache -Recurse -Force -ErrorAction SilentlyContinue }

# ---- console: short and to the point
Write-Host ''
foreach ($s in ($summaryRows | Sort-Object Service)) {
    if (-not $s.Type) { Write-Host ("  {0,-28} NOT FOUND" -f $s.Service) -ForegroundColor Yellow; continue }
    $list = if ($s.Consumers -le 8) { $s.ConsumerRepos } else { (($s.ConsumerRepos -split '; ' | Select-Object -First 8) -join '; ') + "; ... (+$($s.Consumers - 8) more)" }
    Write-Host ("  {0,-28} {1,-5} hosted by {2,-22} {3} consumer(s){4}" -f $s.Service, $s.Type, $s.HostedBy, $s.Consumers, $(if ($s.Consumers) { ": $list" } else { '' })) -ForegroundColor $(if ($s.Consumers) { 'Green' } else { 'Yellow' })
}
Write-Host ''
foreach ($f in $outFiles) { Write-Host "  wrote $f" }
