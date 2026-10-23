<#
.SYNOPSIS
    One-shot entry point: name a service -> discover its contract -> find its consumers across a folder of repos.

.DESCRIPTION
    You give it:
      -Service   one or more names, file names or full paths (comma separated), e.g.
                 OrderService.svc   IOrderService.cs   IOrderService   OrdersController   D:\repos\Orders\Web\OrderService.svc
      -ServiceList  optional text file with one entry per line
      -RootPath  the folder that contains your (100s of) repos

    It then
      1. scans all repos (Get-EndpointInventory.ps1; result is cached in -WorkPath, reuse with no -Rescan)
      2. discovers which endpoint(s) the name refers to by matching it against: the .svc file, the contract file
         (IOrderService.cs), the interface, the implementing class, generated client classes, the SOAP contract
         name and (REST) controller names
      3. prints what it found (contract, operations, hosting repo(s), known proxy repos) so you can check it
      4. runs the usage search (Find-EndpointUsage.ps1) for exactly those endpoints

    If the name matches several unrelated contracts (same interface name in different services) ALL are searched and
    reported separately - narrow it down with -InRepo <repo that hosts/defines the one you mean>.

.EXAMPLE
    .\Find-ServiceConsumers.ps1 -RootPath D:\repos -Service OrderService.svc
.EXAMPLE
    # a list, full paths, and a list file can be mixed
    .\Find-ServiceConsumers.ps1 -RootPath D:\repos -Service OrderService.svc,D:\repos\Billing\Web\BillingService.svc -ServiceList .\services.txt
.EXAMPLE
    .\Find-ServiceConsumers.ps1 -RootPath D:\repos -Service OrderService.svc,IOrderService.cs -InRepo Orders.Service -HostMapPath .\hosts.csv
.EXAMPLE
    # just show what would be searched
    .\Find-ServiceConsumers.ps1 -RootPath D:\repos -Service IOrderService -DiscoverOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RootPath,
    # Names, file names or full paths; comma/semicolon separated lists are fine:
    #   -Service OrderService.svc,IOrderService.cs,D:\repos\Billing\Web\BillingService.svc
    [string[]]$Service = @(),
    # Optional text file, one entry per line (blank lines and lines starting with # are ignored)
    [string]$ServiceList,
    # Disambiguation: only endpoints hosted / implemented / defined in these repo folder names
    [string[]]$InRepo = @(),
    [string]$WorkPath = (Join-Path (Get-Location) 'service-scan'),
    [string]$HostMapPath,
    [string[]]$ExcludeRepo = @(),
    [double]$DriftThreshold = 0.5,
    [switch]$Rescan,
    [switch]$DiscoverOnly,
    [switch]$IncludeSelf,
    [switch]$IncludeTests,
    [switch]$SkipGit
)

$ErrorActionPreference = 'Continue'
function Split-List($s) { if ([string]::IsNullOrWhiteSpace($s)) { return @() }; return @($s -split ';' | Where-Object { $_ }) }
function Get-Key([string]$s) {
    # 'Orders\Web\OrderService.svc' -> 'orderservice' ; 'IOrderService.cs' -> 'iorderservice'
    $leaf = ($s.Trim() -split '[\\/]')[-1]
    $leaf = $leaf -replace '(?i)\.svc\.cs$', '' -replace '(?i)\.(svc|cs)$', ''
    return $leaf.ToLowerInvariant()
}

# ---- normalise the input list (array, "a,b" strings, list file)
$rawItems = @($Service)
if ($ServiceList) {
    if (-not (Test-Path -LiteralPath $ServiceList)) { throw "ServiceList file not found: $ServiceList" }
    $rawItems += @(Get-Content -LiteralPath $ServiceList | Where-Object { $_.Trim() -and $_.Trim() -notmatch '^#' })
}
$inputs = @()
foreach ($r in $rawItems) { foreach ($t in ($r -split '[,;\r\n]')) { $t = $t.Trim().Trim('"', "'"); if ($t) { $inputs += $t } } }
$inputs = @($inputs | Select-Object -Unique)
if ($inputs.Count -eq 0) { throw 'Nothing to search for: pass -Service (names, file names or paths) and/or -ServiceList.' }

$invDir = Join-Path $WorkPath 'inventory'
$useDir = Join-Path $WorkPath 'usage'
New-Item -ItemType Directory -Force -Path $WorkPath | Out-Null

# ---- 1. inventory (cached)
if ($Rescan -or -not (Test-Path (Join-Path $invDir 'endpoint-clusters.csv'))) {
    Write-Host "Step 1/3  Scanning repos under $RootPath ..." -ForegroundColor Cyan
    $p = @{ RootPath = $RootPath; OutputPath = $invDir; DriftThreshold = $DriftThreshold }
    if ($ExcludeRepo.Count) { $p.ExcludeRepo = $ExcludeRepo }
    if ($IncludeTests) { $p.IncludeTests = $true }
    if ($SkipGit) { $p.SkipGit = $true }
    & (Join-Path $PSScriptRoot 'Get-EndpointInventory.ps1') @p | Out-Host
} else {
    Write-Host "Step 1/3  Re-using cached inventory in $invDir (use -Rescan to refresh)" -ForegroundColor Cyan
}

$clusters = @(Import-Csv (Join-Path $invDir 'endpoint-clusters.csv'))
$inv      = @(Import-Csv (Join-Path $invDir 'endpoint-inventory.csv'))

# ---- 2. discover the contract(s) behind the given name(s)
Write-Host "Step 2/3  Discovering contract(s) for $($inputs.Count) input(s)" -ForegroundColor Cyan
# cluster id -> set of lowercase names it answers to, split by kind so that 'X.svc' / 'X.cs' stay precise
$names = @{}; $svcNames = @{}; $fileNames = @{}
function Add-To($table, $id, $n) {
    if ([string]::IsNullOrWhiteSpace($n)) { return }
    if (-not $table.ContainsKey($id)) { $table[$id] = New-Object 'System.Collections.Generic.HashSet[string]' }
    [void]$table[$id].Add((Get-Key $n))
}
function Add-Name($id, $n)     { Add-To $names $id $n }
function Add-SvcName($id, $n)  { Add-To $names $id $n; Add-To $svcNames $id $n }
function Add-FileName($id, $n) { Add-To $names $id $n; Add-To $fileNames $id $n }
foreach ($c in $clusters) {
    Add-Name $c.ClusterId $c.Name
    foreach ($x in (Split-List $c.InterfaceNames)) { Add-Name $c.ClusterId $x }
    foreach ($x in (Split-List $c.ClientClasses))  { Add-Name $c.ClusterId $x }
    foreach ($x in (Split-List $c.SvcFiles))       { Add-SvcName $c.ClusterId $x }
    if ($c.Kind -eq 'REST') { foreach ($x in (Split-List $c.Operations)) { Add-Name $c.ClusterId ($x -split '\.')[0] } }
}
foreach ($r in $inv) {
    foreach ($x in (Split-List $r.ImplClasses)) { Add-Name $r.ClusterId $x }                 # OrderService (implementing class)
    if ($r.Role -ne 'ProxyClient') { Add-FileName $r.ClusterId $r.File }   # IOrderService.cs (contract file) / controller file
}

# Each input becomes: key (file name without extension), kind, and - if it is a path - the repo it lives in.
#   'OrderService.svc' -> .svc files only; 'IOrderService.cs' -> source files only; bare name -> everything.
#   A path such as D:\repos\Orders.Service\Web\OrderService.svc also pins the search to repo 'Orders.Service'.
$repoRows = @(); if (Test-Path (Join-Path $invDir 'repos.csv')) { $repoRows = @(Import-Csv (Join-Path $invDir 'repos.csv')) }
function Get-RepoFromPath([string]$path) {
    if ($path -notmatch '[\\/]') { return '' }
    $norm = ($path -replace '/', '\').ToLowerInvariant()
    $best = ''; $bestLen = 0
    foreach ($r in $repoRows) {                                   # 1) path is inside a scanned repo folder
        $d = ($r.Dir -replace '/', '\').TrimEnd('\').ToLowerInvariant()
        if ($d -and $norm.StartsWith($d + '\') -and $d.Length -gt $bestLen) { $best = $r.Name; $bestLen = $d.Length }
    }
    if ($best) { return $best }
    $segs = $path -split '[\\/]'                                  # 2) otherwise: any path segment equal to a repo folder name (other machine / other layout)
    foreach ($sg in $segs) { foreach ($r in $repoRows) { if ($r.Name -ieq $sg) { return $r.Name } } }
    return ''
}
$wanted = @($inputs | ForEach-Object {
    [pscustomobject]@{
        Raw = $_; Key = (Get-Key $_); Repo = (Get-RepoFromPath $_)
        Kind = $(if ($_ -match '(?i)\.svc$') { 'svc' } elseif ($_ -match '(?i)\.cs$') { 'file' } else { 'any' })
        Matched = (New-Object System.Collections.Generic.List[string])
    }
})
Write-Host 'Input interpreted as:'
foreach ($w in $wanted) { Write-Host ("  {0,-60} name='{1}'  type={2}{3}" -f $w.Raw, $w.Key, $w.Kind, $(if ($w.Repo) { "  repo=$($w.Repo)" } else { '' })) }

function Get-ClusterRepos($c) { return @(@(Split-List $c.HostRepos) + @(Split-List $c.ImplRepos) + @(Split-List $c.DefinitionRepos) + @($c.Owner)) }
function Test-Wanted($c) {
    $id = $c.ClusterId; $any = $false; $cr = $null
    foreach ($w in $wanted) {
        $t = switch ($w.Kind) { 'svc' { $svcNames } 'file' { $fileNames } default { $names } }
        if (-not ($t.ContainsKey($id) -and $t[$id].Contains($w.Key))) { continue }
        if ($w.Repo) { if ($null -eq $cr) { $cr = Get-ClusterRepos $c }; if ($cr -notcontains $w.Repo) { continue } }   # path pins the repo
        $w.Matched.Add($id); $any = $true
    }
    return $any
}
$hits = @($clusters | Where-Object {
    $ok = Test-Wanted $_
    if ($ok -and $InRepo.Count -gt 0) { $ok = @(Get-ClusterRepos $_ | Where-Object { $InRepo -contains $_ }).Count -gt 0 }
    $ok })

# per-input result
$missing = @($wanted | Where-Object { $_.Matched.Count -eq 0 })
if ($hits.Count -gt 0 -and $missing.Count -gt 0) {
    Write-Host ''
    Write-Host "Not found ($($missing.Count) of $($wanted.Count) inputs) - check spelling / path / -InRepo:" -ForegroundColor Yellow
    foreach ($m in $missing) { Write-Host "  - $($m.Raw)" -ForegroundColor Yellow }
}
$wanted | ForEach-Object { [pscustomobject]@{ Input = $_.Raw; Name = $_.Key; Type = $_.Kind; Repo = $_.Repo; MatchedClusters = (($_.Matched | Select-Object -Unique) -join ';') } } |
    Export-Csv -Path (Join-Path $WorkPath 'input-resolution.csv') -NoTypeInformation -Encoding UTF8

if ($hits.Count -eq 0) {
    Write-Host "No endpoint matched: $($inputs -join ', ')" -ForegroundColor Yellow
    if (@($wanted | Where-Object Kind -ne 'any').Count -gt 0) { Write-Host "(An extension restricts matching: '.svc' = .svc files only, '.cs' = source files only. Try the name without the extension.)" }
    $near = @($clusters | Where-Object {
        $id = $_.ClusterId; $hit = $false
        if ($names.ContainsKey($id)) { foreach ($w in $wanted) { foreach ($n in $names[$id]) { if ($n -like "*$($w.Key)*" -or $w.Key -like "*$n*") { $hit = $true } } } }
        $hit } | Select-Object -First 10)
    if ($near.Count) {
        Write-Host 'Closest matches:'
        foreach ($n in $near) { Write-Host ("  [{0}] {1} ({2})  owner: {3}  interfaces: {4}  svc: {5}" -f $n.ClusterId, $n.Name, $n.Kind, $n.Owner, $n.InterfaceNames, $n.SvcFiles) }
    }
    if ($InRepo.Count) { Write-Host "(-InRepo $($InRepo -join ',') is applied - remove it to widen the search; check names against repos.csv)" }
    return
}

$hits | Select-Object ClusterId, Kind, Name, Owner, OwnerConfidence, HostRepos, DefinitionRepos, ProxyRepos, SvcFiles, OperationCount, Flags |
    Export-Csv -Path (Join-Path $WorkPath 'discovered-endpoints.csv') -NoTypeInformation -Encoding UTF8

foreach ($h in $hits) {
    Write-Host ''
    Write-Host ("  [{0}] {1}  ({2})" -f $h.ClusterId, $h.Name, $h.Kind) -ForegroundColor Green
    if ($h.Kind -eq 'WCF') {
        Write-Host ("      SOAP namespace : {0}" -f $h.WireNamespace)
        Write-Host ("      Operations     : {0}" -f $h.Operations)
        Write-Host ("      .svc file(s)   : {0}" -f $h.SvcFiles)
    } else { Write-Host ("      Route          : {0}  ({1})" -f $h.Route, $h.HttpVerb) }
    Write-Host ("      Hosted by      : {0}   (owner: {1}, confidence {2})" -f $h.HostRepos, $h.Owner, $h.OwnerConfidence)
    Write-Host ("      Defined in     : {0}" -f $h.DefinitionRepos)
    if ($h.ProxyRepos) { Write-Host ("      Known proxies  : {0}" -f $h.ProxyRepos) }
    if ($h.Flags)      { Write-Host ("      Flags          : {0}" -f $h.Flags) -ForegroundColor Yellow }
}
if ($hits.Count -gt 1) {
    Write-Host ''
    Write-Host "NOTE: $($hits.Count) different endpoints share this name. All are searched and reported separately (by ClusterId)." -ForegroundColor Yellow
    Write-Host "      To search only one, add -InRepo <repo that hosts it>." -ForegroundColor Yellow
}
Write-Host ''
if ($DiscoverOnly) { Write-Host "Discovery only - saved $(Join-Path $WorkPath 'discovered-endpoints.csv')"; return }

# ---- 3. usage search for exactly those endpoints
Write-Host "Step 3/3  Searching all repos for consumers of $($hits.Count) endpoint(s) ..." -ForegroundColor Cyan
$u = @{ RootPath = $RootPath; InventoryPath = $invDir; OutputPath = $useDir; ClusterId = @($hits | ForEach-Object { $_.ClusterId }) }
if ($HostMapPath) { $u.HostMapPath = $HostMapPath }
if ($ExcludeRepo.Count) { $u.ExcludeRepo = $ExcludeRepo }
if ($IncludeSelf) { $u.IncludeSelf = $true }
if ($IncludeTests) { $u.IncludeTests = $true }
& (Join-Path $PSScriptRoot 'Find-EndpointUsage.ps1') @u | Out-Host

Write-Host ''
Write-Host 'Start with:' -ForegroundColor Cyan
Write-Host "  $(Join-Path $useDir 'cluster-usage-rollup.csv')   (who consumes each endpoint, which operations)"
Write-Host "  $(Join-Path $useDir 'consumer-summary.csv')       (endpoint x consumer repo)"
Write-Host "  $(Join-Path $useDir 'usage-detail.csv')           (every file/line of evidence)"
