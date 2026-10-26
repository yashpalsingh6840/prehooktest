<#
.SYNOPSIS
    STEP 2 - Find who consumes the endpoints discovered by Get-EndpointInventory.ps1.

.DESCRIPTION
    Reads the cluster list from step 1 and scans every repo for consumers. Every hit is *attributed* to a specific
    cluster using evidence, so 'IOrderService' in repo X is only counted against the contract that repo actually uses:

      WCF  1. Generated / manual client proxies recorded in step 1                       (High)
           2. <client><endpoint contract=.. address=..> in *.config                       (High when contract+address agree)
           3. Identifier hits (interface name, generated client class name) resolved by:
                Local        repo declares / proxies that exact contract                  (High)
                ProjectRef   repo references the NuGet/project that defines the contract  (High)
                UsingNamespace  file imports the contract's namespace                     (Medium)
                NameOnly     only one contract has that name                              (Low)
                Ambiguous    several contracts share the name, nothing disambiguates      (Low, all candidates listed)
           4. Operation-level calls (.GetOrder( / .GetOrderAsync() inside files already attributed to the contract
           5. '.svc' URL references in any text file (config, json, yaml, scripts ...)
           6. Repos that reference the contract library but show no code usage             (Low)

      REST 1. String literals matching the route (segment-wise; {id} matches anything), full URL or prefix + "/" + id
           2. HTTP verb inferred from the surrounding code (GetAsync/PostAsJsonAsync/HttpMethod.X/[Get(..)]) to separate
              GET /orders/{id} from DELETE /orders/{id}
           3. Optional -HostMapPath (Repo,Host) lets a matching host raise confidence and a *different* host lower it -
              the best signal for telling duplicate routes in different services apart.

    Hits in the owning repo itself (and in definition lines of controllers / contracts) are excluded unless -IncludeSelf.

.OUTPUTS (in -OutputPath)
    usage-detail.csv          every piece of evidence (file, line, snippet, confidence, how it was resolved)
    consumer-summary.csv      endpoint x consumer repo, best confidence, evidence types, operations used
    cluster-usage-rollup.csv  per endpoint: consumer list, operations seen / not seen, review hints
    host-references.csv       (only with -HostMapPath) repos mentioning a known service host name

.EXAMPLE
    .\Find-EndpointUsage.ps1 -RootPath D:\repos -InventoryPath D:\scan\out
.EXAMPLE
    # only the endpoints owned by two repos, with host names to sharpen REST matching
    .\Find-EndpointUsage.ps1 -RootPath D:\repos -InventoryPath D:\scan\out -OwnerRepo Orders.Service,Orders.Api -HostMapPath .\hosts.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RootPath,
    [Parameter(Mandatory)][string]$InventoryPath,
    [string]$OutputPath = (Join-Path (Get-Location) 'usage-output'),
    # Scope: only analyse endpoints owned/hosted/defined by these repos (the migration scope), or specific cluster ids.
    [string[]]$OwnerRepo = @(),
    [string[]]$ClusterId = @(),
    # Optional CSV with columns Repo,Host (host may be a name or URL; several rows per repo allowed).
    [string]$HostMapPath,
    [string[]]$ExcludeRepo = @(),
    [string[]]$ExcludeDir = @('bin', 'obj', 'node_modules', '.git', '.vs', '.idea', 'packages', 'TestResults', 'artifacts', 'dist', '.next', '.angular', 'coverage'),
    [string[]]$TextExtensions = @('.cs', '.vb', '.cshtml', '.razor', '.config', '.json', '.xml', '.yml', '.yaml', '.ps1', '.psm1', '.tf', '.tfvars', '.bicep', '.sh', '.wsdl', '.svcmap', '.http', '.settings', '.ini', '.properties',
        '.js', '.jsx', '.mjs', '.ts', '.tsx', '.vue', '.svelte', '.html', '.htm', '.aspx', '.ascx'),
    [switch]$IncludeSelf,
    [switch]$IncludeTests,
    [int]$MaxFileSizeMB = 5
)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'EndpointScan.Common.ps1')
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

function Split-List($s) { if ([string]::IsNullOrWhiteSpace($s)) { return @() }; return @($s -split ';' | Where-Object { $_ }) }
$rank = @{ High = 3; Medium = 2; Low = 1 }

# ------------------------------------------------------------------------------------------------
# Load step-1 output
# ------------------------------------------------------------------------------------------------
foreach ($f in 'endpoint-clusters.csv', 'endpoint-inventory.csv') {
    if (-not (Test-Path (Join-Path $InventoryPath $f))) { throw "Missing $f in $InventoryPath - run Get-EndpointInventory.ps1 first." }
}
$clustersAll = @(Import-Csv (Join-Path $InventoryPath 'endpoint-clusters.csv'))
$inv         = @(Import-Csv (Join-Path $InventoryPath 'endpoint-inventory.csv'))
$refRows     = if (Test-Path (Join-Path $InventoryPath 'repo-references.csv')) { @(Import-Csv (Join-Path $InventoryPath 'repo-references.csv')) } else { @() }
$repoFile    = Join-Path $InventoryPath 'repos.csv'

$repos = New-Object System.Collections.Generic.List[object]
if (Test-Path $repoFile) {
    foreach ($r in (Import-Csv $repoFile)) {
        $dir = if (Test-Path -LiteralPath $r.Dir) { $r.Dir } else { Join-Path $RootPath $r.Name }
        if ((Test-Path -LiteralPath $dir) -and ($ExcludeRepo -notcontains $r.Name)) { $repos.Add([pscustomobject]@{ Name = $r.Name; Dir = $dir }) }
        elseif ($ExcludeRepo -notcontains $r.Name) { Write-Warning "Repo folder for '$($r.Name)' not found; skipped." }
    }
} else {
    foreach ($d in (Find-Repos -Root $RootPath -ExcludeDir $ExcludeDir -ExcludeRepo $ExcludeRepo)) { $repos.Add([pscustomobject]@{ Name = (Split-Path $d -Leaf); Dir = $d }) }
}

$repoRefs = @{}
foreach ($r in $refRows) { if (-not $repoRefs.ContainsKey($r.Repo)) { $repoRefs[$r.Repo] = New-CiSet }; [void]$repoRefs[$r.Repo].Add($r.RefName) }
function Get-RepoRefs([string]$repo) { if ($repoRefs.ContainsKey($repo)) { return , $repoRefs[$repo] } else { return , (New-CiSet) } }

# host map
$hostsByRepo = @{}
if ($HostMapPath) {
    foreach ($r in (Import-Csv $HostMapPath)) {
        foreach ($h in ($r.Host -split ';')) {
            $h = $h.Trim(); if (-not $h) { continue }
            if ($h -match '^[a-z]+://') { try { $h = ([uri]$h).Host } catch { } }
            if (-not $hostsByRepo.ContainsKey($r.Repo)) { $hostsByRepo[$r.Repo] = New-CiSet }
            [void]$hostsByRepo[$r.Repo].Add($h.ToLowerInvariant())
        }
    }
}

# ------------------------------------------------------------------------------------------------
# Build cluster model
# ------------------------------------------------------------------------------------------------
$cl = @{}
foreach ($r in $clustersAll) {
    $self = @((@(Split-List $r.HostRepos) + @(Split-List $r.ImplRepos) + @(Split-List $r.DefinitionRepos)) | Select-Object -Unique)
    $inScope = $true
    if ($ClusterId.Count -gt 0 -and $ClusterId -notcontains $r.ClusterId) { $inScope = $false }
    if ($OwnerRepo.Count -gt 0 -and -not ($OwnerRepo -contains $r.Owner -or @($self | Where-Object { $OwnerRepo -contains $_ }).Count -gt 0)) { $inScope = $false }
    $o = [pscustomobject]@{
        Id = $r.ClusterId; Kind = $r.Kind; Name = $r.Name; Owner = $r.Owner; Flags = $r.Flags; InScope = $inScope
        SelfRepos = (New-CiSet); HostRepoSet = (New-CiSet); ProxyRepos = (New-CiSet); Ifaces = @(Split-List $r.InterfaceNames); Clients = @(Split-List $r.ClientClasses)
        ClrNs = @(Split-List $r.ClrNamespaces); SvcLeaf = @(Split-List $r.SvcFiles | ForEach-Object { ($_ -split '[\\/]')[-1] })
        Ops = @(Split-List $r.Operations); ProjNames = (New-CiSet); Verb = $r.HttpVerb; RouteNorm = $r.RouteNormalized
        Segs = @(); SegRx = @(); StaticCount = 0; Key = ''; Hosts = (New-CiSet); OpRx = $null
    }
    foreach ($x in $self) { [void]$o.SelfRepos.Add($x) }
    foreach ($x in (Split-List $r.ProxyRepos)) { [void]$o.ProxyRepos.Add($x) }
    foreach ($x in (@(Split-List $r.HostRepos) + @(Split-List $r.ImplRepos))) { [void]$o.HostRepoSet.Add($x) }
    foreach ($x in (Split-List $r.DefinitionProjectNames)) { [void]$o.ProjNames.Add($x) }
    foreach ($x in $self) { if ($hostsByRepo.ContainsKey($x)) { foreach ($h in $hostsByRepo[$x]) { [void]$o.Hosts.Add($h) } } }
    if ($r.Kind -eq 'REST') {
        $o.Segs = @($r.RouteNormalized -split '/' | Where-Object { $_ -ne '' })
        $rx = @()
        foreach ($sg in $o.Segs) {
            if ($sg -eq '{}') { $rx += '*' }
            elseif ($sg.Contains('{}')) { $rx += (New-Object Regex(('^' + (($sg -split [regex]::Escape('{}') | ForEach-Object { [regex]::Escape($_) }) -join '.+') + '$'))) }
            else { $rx += $null }
        }
        $o.SegRx = $rx
        $n = 0; foreach ($sg in $o.Segs) { if ($sg.Contains('{')) { break }; $n++ }
        $o.StaticCount = $n
        if ($n -ge 2) { $o.Key = $o.Segs[0] + '/' + $o.Segs[1] } elseif ($n -eq 1) { $o.Key = $o.Segs[0] }
    }
    $cl[$o.Id] = $o
}
$inScopeClusters = @($cl.Values | Where-Object InScope)
Write-Host ("Clusters loaded: {0} (in scope: {1})" -f $cl.Count, $inScopeClusters.Count)

# index: identifier (interface / generated client class) -> clusters   [ALL clusters, so name collisions resolve correctly]
$identIndex = @{}
foreach ($c in $cl.Values) {
    if ($c.Kind -ne 'WCF') { continue }
    foreach ($n in @($c.Ifaces + $c.Clients)) {
        $k = $n.ToLowerInvariant()
        if (-not $identIndex.ContainsKey($k)) { $identIndex[$k] = New-Object System.Collections.Generic.List[object] }
        if (-not $identIndex[$k].Contains($c)) { $identIndex[$k].Add($c) }
    }
}
$svcIndex = @{}
foreach ($c in $cl.Values) {
    foreach ($n in $c.SvcLeaf) {
        $k = $n.ToLowerInvariant()
        if (-not $svcIndex.ContainsKey($k)) { $svcIndex[$k] = New-Object System.Collections.Generic.List[object] }
        $svcIndex[$k].Add($c)
    }
}
$restIdx = @{}
foreach ($c in $inScopeClusters) {
    if ($c.Kind -ne 'REST' -or -not $c.Key) { continue }
    if (-not $restIdx.ContainsKey($c.Key)) { $restIdx[$c.Key] = New-Object System.Collections.Generic.List[object] }
    $restIdx[$c.Key].Add($c)
}
$notSearchable = @($inScopeClusters | Where-Object { $_.Kind -eq 'REST' -and -not $_.Key })

# local index: "repo|interface" -> cluster ids that repo declares / proxies / implements ; definition files to skip
$localIdx = @{}; $wcfDefFiles = New-CiSet; $restDefFiles = New-CiSet
foreach ($r in $inv) {
    $fileKey = $r.Repo + '|' + $r.File
    if ($r.Kind -eq 'WCF') {
        $k = $r.Repo + '|' + $r.ContractOrController.ToLowerInvariant()
        if (-not $localIdx.ContainsKey($k)) { $localIdx[$k] = New-CiSet }
        [void]$localIdx[$k].Add($r.ClusterId)
        [void]$wcfDefFiles.Add($fileKey)
    } else { [void]$restDefFiles.Add($fileKey) }
}

$identRx = $null
if ($identIndex.Count -gt 0) { $identRx = New-Object Regex(('\b(?:' + (($identIndex.Keys | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'), 'IgnoreCase,Compiled') }
$svcRx = $null
if ($svcIndex.Count -gt 0) { $svcRx = New-Object Regex(('[^\s"''<>]*?(?<svc>' + (($svcIndex.Keys | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?![\w])[^\s"''<>]*'), 'IgnoreCase,Compiled') }
$allHosts = New-CiSet; foreach ($s in $hostsByRepo.Values) { foreach ($h in $s) { [void]$allHosts.Add($h) } }
$hostRx = $null
if ($allHosts.Count -gt 0) { $hostRx = New-Object Regex(('(?<![\w.-])(?:' + (($allHosts | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?![\w-])'), 'IgnoreCase,Compiled') }

$RxLit = New-Object Regex('(?:\$@|@\$|\$|@)?"(?<s>[^"\r\n]{3,300})"|(?<u>https?://[^\s"''<>)]{4,300})', 'Compiled')
$verbGroups = 'v1', 'v2', 'v3', 'v4', 'v5', 'v6', 'v7', 'v8', 'v9'
$RxLitJs = New-Object Regex('"(?<s>[^"\r\n]{3,300})"|''(?<s2>[^''\r\n]{3,300})''|`(?<s3>[^`]{3,300})`|(?<u>https?://[^\s"''`<>)]{4,300})', 'Compiled')
$jsLikeExt = @('.js', '.jsx', '.mjs', '.ts', '.tsx', '.vue', '.svelte', '.html', '.htm', '.cshtml', '.razor', '.aspx', '.ascx', '.ps1', '.sh')
$RxVerbHint = New-Object Regex((@(
    'HttpMethod\.(?<v1>Get|Post|Put|Delete|Patch)\b',
    '\b(?<v2>Get|Post|Put|Delete|Patch)(?:Async|FromJsonAsync|AsJsonAsync|ByteArrayAsync|StringAsync|StreamAsync)\b',
    '\[(?<v3>Get|Post|Put|Delete|Patch)\s*\(',
    'Method\.(?<v4>Get|Post|Put|Delete|Patch)\b',
    '\.(?<v5>Get|Post|Put|Delete|Patch)\s*<',
    'new\s+HttpMethod\s*\(\s*"(?<v6>GET|POST|PUT|DELETE|PATCH)"',
    '(?:[Hh]ttp|axios|[Cc]lient|[Aa]pi|[Rr]equest|[Ff]etch|ajax|\$)\w*\s*\.\s*(?<v7>get|post|put|delete|patch)\s*(?:<[^>]*>)?\s*\(',
    '(?:method|type)\s*[:=]\s*["''`](?<v8>GET|POST|PUT|DELETE|PATCH|get|post|put|delete|patch)["''`]',
    '\.request\s*\(\s*["''](?<v9>get|post|put|delete|patch)["'']'
) -join '|'), 'Compiled')

# ------------------------------------------------------------------------------------------------
# Evidence collection
# ------------------------------------------------------------------------------------------------
$ev = New-Object System.Collections.Generic.List[object]
$hostRefs = New-Object System.Collections.Generic.List[object]
$hostSeen = @{}   # repo -> set of hosts mentioned somewhere in that repo

function Get-LineText([string]$text, [int]$idx) {
    if ($idx -lt 0 -or $idx -ge $text.Length) { return '' }
    $s = $text.LastIndexOf("`n", [Math]::Max(0, $idx - 1)); $e = $text.IndexOf("`n", $idx)
    if ($e -lt 0) { $e = $text.Length }
    $t = $text.Substring($s + 1, $e - $s - 1).Trim()
    if ($t.Length -gt 180) { $t = $t.Substring(0, 180) + '...' }
    return $t
}

function Get-TargetHostRepos($c, [string]$urlHost) {
    # Which hosting repo of this endpoint does the URL host belong to? (needs -HostMapPath)
    if (-not $urlHost) { return '' }
    $t = @($c.HostRepoSet | Where-Object { $hostsByRepo.ContainsKey($_) -and $hostsByRepo[$_].Contains($urlHost.ToLowerInvariant()) })
    return ($t -join ';')
}

function Add-Ev($c, [string]$repo, [string]$rel, [int]$line, [string]$type, [string]$conf, [string]$res, [string]$op, [string]$snip, [string]$cands, [string]$matched, [string]$urlHost = '') {
    if (-not $c.InScope) { return }
    $isSelf = $c.SelfRepos.Contains($repo)
    if ($isSelf) {
        if (-not $IncludeSelf) { return }
        # Inside the home repo only genuine *client-side* use counts for SOAP (own client proxy / client endpoint config);
        # the implementation, host config and wiring all mention the contract without being consumers.
        if ($c.Kind -eq 'WCF' -and @('GeneratedProxy', 'ManualClientProxy', 'ConfigClientEndpoint') -notcontains $type) { return }
    }
    $ev.Add([pscustomobject][ordered]@{
        ClusterId = $c.Id; Kind = $c.Kind; Endpoint = $c.Name; Owner = $c.Owner; ConsumerRepo = $repo; File = $rel; Line = $line
        EvidenceType = $type; Operation = $op; Confidence = $conf; Resolution = $res; Candidates = $cands; TargetHostRepo = (Get-TargetHostRepos $c $urlHost); Matched = $matched; Snippet = $snip; IsSelf = $isSelf
    })
}

function Resolve-Ident([string]$repo, [string]$name, [string]$text) {
    $key = $name.ToLowerInvariant()
    if (-not $identIndex.ContainsKey($key)) { return $null }
    $cands = $identIndex[$key].ToArray()
    $loc = @($cands | Where-Object { ($localIdx.ContainsKey("$repo|$key") -and $localIdx["$repo|$key"].Contains($_.Id)) -or $_.ProxyRepos.Contains($repo) })
    if ($loc.Count -gt 0) { return [pscustomobject]@{ Clusters = $loc; Method = 'Local'; Confidence = 'High' } }
    $refs = Get-RepoRefs $repo
    $byRef = @($cands | Where-Object { $c = $_; @($c.ProjNames | Where-Object { $refs.Contains($_) }).Count -gt 0 })
    if ($byRef.Count -gt 0) { return [pscustomobject]@{ Clusters = $byRef; Method = 'ProjectRef'; Confidence = 'High' } }
    $byUsing = @($cands | Where-Object {
        $c = $_; $hit = $false
        foreach ($ns in $c.ClrNs) {
            if ([regex]::IsMatch($text, '\busing\s+(?:static\s+)?' + [regex]::Escape($ns) + '\s*;') -or [regex]::IsMatch($text, [regex]::Escape($ns + '.' + $name) + '\b')) { $hit = $true; break }
        }
        $hit })
    if ($byUsing.Count -gt 0) { return [pscustomobject]@{ Clusters = $byUsing; Method = 'UsingNamespace'; Confidence = 'Medium' } }
    if ($cands.Count -eq 1) { return [pscustomobject]@{ Clusters = $cands; Method = 'NameOnly'; Confidence = 'Low' } }
    return [pscustomobject]@{ Clusters = $cands; Method = 'Ambiguous'; Confidence = 'Low' }
}

function Get-OpRx($c) {
    if ($null -ne $c.OpRx) { return $c.OpRx }
    if ($c.Ops.Count -eq 0) { $c.OpRx = $false; return $false }
    $c.OpRx = New-Object Regex(('\.\s*(?<op>' + (($c.Ops | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?:Async)?\s*\('), 'Compiled')
    return $c.OpRx
}

function Get-LitParts([string]$s) {
    if ($s.IndexOf('/') -lt 0) { return $null }
    if ($s -match '^(application|text|image|multipart|audio|video)/' -or $s -match 'schemas\.|w3\.org|tempuri\.org|^</|\\') { return $null }
    $litHost = ''; $path = $s
    $m = [regex]::Match($s, '^[a-zA-Z][a-zA-Z0-9+.-]*://([^/\s?#]+)(/[^?#]*)?')
    if ($m.Success) { $litHost = ($m.Groups[1].Value -replace ':\d+$', '').ToLowerInvariant(); $path = $m.Groups[2].Value }
    $path = ($path -split '[?#]')[0]
    $trail = $path.TrimEnd().EndsWith('/')
    $path = [regex]::Replace($path, '\{\{[^}]*\}\}|\$?\{[^}]*\}', '{}')
    $segs = @($path.Trim('~', '/').ToLowerInvariant() -split '/' | Where-Object { $_ -ne '' })
    while ($segs.Count -gt 0 -and $segs[0] -eq '{}') { if ($segs.Count -gt 1) { $segs = $segs[1..($segs.Count - 1)] } else { $segs = @() } }
    if ($segs.Count -eq 0) { return $null }
    return [pscustomobject]@{ Host = $litHost; Segs = $segs; Trail = $trail }
}

function Test-RouteMatch($c, $lit, [bool]$trail = $false) {
    $n = [Math]::Min($lit.Count, $c.Segs.Count)
    for ($i = 0; $i -lt $n; $i++) {
        $p = $c.SegRx[$i]; $sg = $lit[$i]
        if ($null -eq $p) { if ($c.Segs[$i] -ne $sg -and $sg -ne '{}') { return $null } }
        elseif ($p -is [string]) { }                       # pure {param}: anything matches
        else { if (-not $p.IsMatch($sg)) { return $null } }
    }
    if ($lit.Count -eq $c.Segs.Count) { return 'Full' }
    if ($lit.Count -lt $c.Segs.Count) {
        # "api/orders/" + id : trailing slash and exactly one {param} segment left = the complete route, id appended at runtime
        if ($trail -and $c.Segs.Count -eq $lit.Count + 1 -and $c.SegRx[$lit.Count] -is [string]) { return 'Full' }
        return 'Prefix'
    }
    return $null
}

# --- 1. evidence already known from step 1 (generated / manual client proxies)
foreach ($g in ($inv | Where-Object { $_.Kind -eq 'WCF' -and $_.Role -eq 'ProxyClient' } | Group-Object ClusterId, Repo, File)) {
    $r = $g.Group[0]
    $type = if ($r.IsGenerated -eq 'True') { 'GeneratedProxy' } else { 'ManualClientProxy' }
    Add-Ev $cl[$r.ClusterId] $r.Repo $r.File ([int]$r.Line) $type $r.Confidence $r.Resolution '*' ("Client proxy for " + $r.ContractOrController) '' ''
}

# ------------------------------------------------------------------------------------------------
# Scan every repo once
# ------------------------------------------------------------------------------------------------
$repoNo = 0
foreach ($repo in $repos) {
    $repoNo++
    Write-Progress -Activity 'Scanning for consumers' -Status "$($repo.Name) ($repoNo/$($repos.Count))" -PercentComplete (100 * $repoNo / $repos.Count)
    $rootLen = $repo.Dir.Length
    foreach ($f in (Get-RepoFiles -Dir $repo.Dir -ExcludeDir $ExcludeDir)) {
        $ext = [System.IO.Path]::GetExtension($f).ToLowerInvariant()
        if ($TextExtensions -notcontains $ext) { continue }
        $leaf = [System.IO.Path]::GetFileName($f)
        if ($leaf -match '(?i)package-lock\.json|\.min\.|\.bundle\.|\.d\.ts$|\.(g|designer)\.cs$|AssemblyInfo\.cs$|launchSettings\.json$|^(swagger|openapi)[^\\/]*\.(json|ya?ml)$') { continue }
        $rel = ($f.Substring($rootLen).TrimStart('\', '/')) -replace '\\', '/'
        if (-not $IncludeTests -and (Test-IsTestPath $rel)) { continue }
        $text = Read-Text $f $MaxFileSizeMB
        if (-not $text) { continue }
        $isCode = $ext -in '.cs', '.vb', '.cshtml', '.razor'
        $jsLike = $jsLikeExt -contains $ext
        if ($ext -eq '.cs') { $clean = ([CsMasker]::Process($text))[0] }
        elseif ($ext -in '.js', '.jsx', '.mjs', '.ts', '.tsx', '.vue', '.svelte') { $clean = [regex]::Replace($text, '(?m)^[ \t]*(?://|/\*|\*).*$', [System.Text.RegularExpressions.MatchEvaluator]{ param($m) ' ' * $m.Length }) }
        else { $clean = $text }
        $fileKey = $repo.Name + '|' + $rel
        $attributed = @{}    # cluster id -> @{ Cluster; Conf }  for operation-level search

        # ---- host name mentions (feeds confidence boost + host-references.csv)
        if ($hostRx -and $hostRx.IsMatch($clean)) {
            foreach ($hm in $hostRx.Matches($clean)) {
                $h = $hm.Value.ToLowerInvariant()
                if (-not $hostSeen.ContainsKey($repo.Name)) { $hostSeen[$repo.Name] = New-CiSet }
                if ($hostSeen[$repo.Name].Add($h)) {
                    $owners = @($hostsByRepo.Keys | Where-Object { $hostsByRepo[$_].Contains($h) })
                    $hostRefs.Add([pscustomobject]@{ ConsumerRepo = $repo.Name; Host = $h; OwnerRepos = ($owners -join ';'); File = $rel; Line = [CsMasker]::Line($text, $hm.Index); Snippet = (Get-LineText $text $hm.Index) })
                }
            }
        }

        # ---- WCF: identifier hits (skip files that ARE definitions/proxies - step 1 already recorded those)
        if ($isCode -and $identRx -and -not $wcfDefFiles.Contains($fileKey)) {
            $hits = [ordered]@{}
            foreach ($m in $identRx.Matches($clean)) {
                $k = $m.Value.ToLowerInvariant()
                if (-not $hits.Contains($k)) { $hits[$k] = @{ Idx = $m.Index; Name = $m.Value; N = 0 } }
                $hits[$k].N++
            }
            foreach ($k in $hits.Keys) {
                $h = $hits[$k]
                $res = Resolve-Ident $repo.Name $h.Name $clean
                if (-not $res) { continue }
                $candIds = ($res.Clusters | ForEach-Object { $_.Id }) -join ';'
                foreach ($c in $res.Clusters) {
                    if ($res.Method -ne 'Ambiguous' -and -not $c.SelfRepos.Contains($repo.Name)) { $attributed[$c.Id] = @{ Cluster = $c; Conf = $res.Confidence } }
                    $cand = if ($res.Method -eq 'Ambiguous') { $candIds } else { '' }
                    Add-Ev $c $repo.Name $rel ([CsMasker]::Line($text, $h.Idx)) 'CodeReference' $res.Confidence $res.Method '' (Get-LineText $text $h.Idx) $cand ("{0} (x{1})" -f $h.Name, $h.N)
                }
            }
            # operation-level: calls to the contract's operations inside files already tied to that contract
            foreach ($a in $attributed.Values) {
                $orx = Get-OpRx $a.Cluster
                if (-not $orx) { continue }
                $seen = @{}
                foreach ($om in $orx.Matches($clean)) {
                    $op = $om.Groups['op'].Value
                    if ($seen.ContainsKey($op)) { continue }; $seen[$op] = $true
                    $opConf = if ($a.Conf -eq 'High') { 'Medium' } else { 'Low' }
                    Add-Ev $a.Cluster $repo.Name $rel ([CsMasker]::Line($text, $om.Index)) 'OperationCall' $opConf 'OpNameInAttributedFile' $op (Get-LineText $text $om.Index) '' $op
                }
            }
        }

        # ---- WCF: <client><endpoint> in config files
        if ($ext -eq '.config' -and $text.Contains('<client')) {
            try { $doc = [xml]$text } catch { $doc = $null }
            if ($doc) {
                foreach ($ep in @($doc.SelectNodes('//system.serviceModel/client/endpoint'))) {
                    $contract = $ep.GetAttribute('contract'); $addr = $ep.GetAttribute('address')
                    if (-not $contract -or $contract -eq 'IMetadataExchange') { continue }
                    $short = Get-ShortName $contract
                    $leafAddr = ''
                    if ($addr) { $leafAddr = (($addr -split '[?#]')[0] -split '/')[-1].ToLowerInvariant() }
                    $byAddr = if ($leafAddr -and $svcIndex.ContainsKey($leafAddr)) { $svcIndex[$leafAddr].ToArray() } else { @() }
                    $res = Resolve-Ident $repo.Name $short $text
                    $idx = $text.IndexOf($contract); $line = if ($idx -ge 0) { [CsMasker]::Line($text, $idx) } else { 1 }
                    $snip = "contract=$contract address=$addr"
                    $addrHost = ''; $am = [regex]::Match($addr, '^[a-zA-Z][a-zA-Z0-9+.-]*://([^/:\s]+)'); if ($am.Success) { $addrHost = $am.Groups[1].Value.ToLowerInvariant() }
                    if ($res -and $res.Method -ne 'Ambiguous') {
                        foreach ($c in $res.Clusters) {
                            $both = @($byAddr | Where-Object { $_.Id -eq $c.Id }).Count -gt 0
                            Add-Ev $c $repo.Name $rel $line 'ConfigClientEndpoint' $(if ($both) { 'High' } else { $res.Confidence }) $(if ($both) { 'Contract+Address' } else { $res.Method }) '' $snip '' $contract $addrHost
                        }
                    } elseif (@($byAddr).Count -gt 0) {
                        $cids = ($byAddr | ForEach-Object { $_.Id }) -join ';'
                        foreach ($c in $byAddr) { Add-Ev $c $repo.Name $rel $line 'ConfigClientEndpoint' $(if (@($byAddr).Count -eq 1) { 'Medium' } else { 'Low' }) 'AddressOnly' '' $snip $(if (@($byAddr).Count -gt 1) { $cids } else { '' }) $addr $addrHost }
                    } elseif ($res) {
                        $cids = ($res.Clusters | ForEach-Object { $_.Id }) -join ';'
                        foreach ($c in $res.Clusters) { Add-Ev $c $repo.Name $rel $line 'ConfigClientEndpoint' 'Low' 'Ambiguous' '' $snip $cids $contract }
                    }
                }
            }
        }

        # ---- WCF: *.svc URL mentions anywhere (config, json, yaml, scripts, code)
        if ($svcRx -and $clean.IndexOf('.svc', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $seenSvc = @{}
            foreach ($sm in $svcRx.Matches($clean)) {
                $leafName = $sm.Groups['svc'].Value.ToLowerInvariant()
                if ($seenSvc.ContainsKey($leafName)) { continue }; $seenSvc[$leafName] = $true
                $urlHost = ''; $um = [regex]::Match($sm.Value, '^https?://([^/:\s]+)'); if ($um.Success) { $urlHost = $um.Groups[1].Value.ToLowerInvariant() }
                $cs = $svcIndex[$leafName].ToArray()
                if ($urlHost) {   # the URL host clearly belongs to some of the candidates -> don't report the others as mismatches
                    $hm2 = @($cs | Where-Object { $_.Hosts.Contains($urlHost) })
                    if ($hm2.Count -gt 0) { $cs = $hm2 }
                }
                $cids = ($cs | ForEach-Object { $_.Id }) -join ';'
                foreach ($c in $cs) {
                    $conf = 'Medium'; $how = 'SvcName'
                    if ($cs.Count -gt 1) { $conf = 'Low'; $how = 'SvcNameShared' }
                    if ($urlHost -and $c.Hosts.Count -gt 0) { if ($c.Hosts.Contains($urlHost)) { $conf = 'High'; $how = 'SvcName+Host' } else { $conf = 'Low'; $how = 'SvcName+HostMismatch' } }
                    Add-Ev $c $repo.Name $rel ([CsMasker]::Line($text, $sm.Index)) 'ServiceUrlReference' $conf $how '' (Get-LineText $text $sm.Index) $(if ($cs.Count -gt 1) { $cids } else { '' }) $sm.Value $urlHost
                }
            }
        }

        # ---- REST: route string literals
        if ($restIdx.Count -gt 0 -and $clean.IndexOf('/') -ge 0) {
            $isRestDef = $restDefFiles.Contains($fileKey)
            $done = @{}
            $litRx = if ($jsLike) { $RxLitJs } else { $RxLit }
            foreach ($lm in $litRx.Matches($clean)) {
                $s = ''; foreach ($gn in 's', 's2', 's3', 'u') { if ($lm.Groups[$gn].Success) { $s = $lm.Groups[$gn].Value; break } }
                $parts = Get-LitParts $s
                if (-not $parts) { continue }
                $keys = @(); if ($parts.Segs.Count -ge 2) { $keys += ($parts.Segs[0] + '/' + $parts.Segs[1]) }; $keys += $parts.Segs[0]
                $found = New-Object System.Collections.Generic.List[object]
                foreach ($k in $keys) {
                    if (-not $restIdx.ContainsKey($k)) { continue }
                    foreach ($c in $restIdx[$k]) {
                        $lvl = Test-RouteMatch $c $parts.Segs $parts.Trail
                        if ($lvl) { $found.Add(@{ C = $c; Lvl = $lvl }) }
                    }
                }
                if ($found.Count -eq 0) { continue }
                $lineTxt = Get-LineText $text $lm.Index
                if ($isRestDef -and ($lineTxt -match '\[[^\]]*\b(Route|RoutePrefix|Http(Get|Post|Put|Delete|Patch|Head|Options)|AcceptVerbs)\b' -or $lineTxt -match '\.Map(Get|Post|Put|Delete|Patch|Methods|Group)\s*\(')) { continue }
                # HTTP verb hint: same line first, previous line as fallback (keeps neighbouring statements from polluting the hint)
                $hint = New-Object System.Collections.Generic.HashSet[string]
                $ls = $clean.LastIndexOf("`n", $lm.Index)
                $probe = @($lineTxt); if ($ls -gt 0) { $probe += (Get-LineText $text ($ls - 1)) }
                $le = $clean.IndexOf("`n", $lm.Index); if ($le -ge 0 -and $le + 1 -lt $text.Length) { $probe += (Get-LineText $text ($le + 1)) }
                foreach ($pt in $probe) {
                    foreach ($vm in $RxVerbHint.Matches($pt)) { foreach ($gn in $verbGroups) { if ($vm.Groups[$gn].Success) { [void]$hint.Add($vm.Groups[$gn].Value.ToUpperInvariant()) } } }
                    if ($hint.Count -gt 0) { break }
                }
                $keep = New-Object System.Collections.Generic.List[object]
                foreach ($fm in $found) {
                    if ($hint.Count -gt 0 -and $fm.C.Verb -ne 'ANY' -and -not $hint.Contains($fm.C.Verb)) { continue }   # verb disambiguation
                    $keep.Add($fm)
                }
                $hasFull = $false; foreach ($fm in $keep) { if ($fm.Lvl -eq 'Full') { $hasFull = $true } }
                foreach ($fm in $keep) {
                    $c = $fm.C; $lvl = $fm.Lvl
                    if ($hasFull -and $lvl -ne 'Full') { continue }   # a full route match beats prefix matches for the same literal
                    $dk = $c.Id + '|' + ($parts.Segs -join '/')
                    if ($done.ContainsKey($dk)) { continue }; $done[$dk] = $true
                    $conf = if ($lvl -eq 'Full') { 'Medium' } else { 'Low' }
                    if ($c.StaticCount -lt 2) { $conf = 'Low' }
                    $how = $lvl + $(if ($hint.Count -gt 0) { '+VerbHint' } else { '' })
                    if ($parts.Host -and $c.Hosts.Count -gt 0) {
                        if ($c.Hosts.Contains($parts.Host)) { $conf = $(if ($lvl -eq 'Full') { 'High' } else { 'Medium' }); $how += '+HostMatch' } else { $conf = 'Low'; $how += '+HostMismatch' }
                    }
                    Add-Ev $c $repo.Name $rel ([CsMasker]::Line($text, $lm.Index)) 'RouteLiteral' $conf $how '' $lineTxt '' $s $parts.Host
                }
            }
        }
    }
}
Write-Progress -Activity 'Scanning for consumers' -Completed

# --- repos that reference the contract library but showed no code usage: weak but useful evidence
foreach ($c in ($inScopeClusters | Where-Object { $_.Kind -eq 'WCF' -and $_.ProjNames.Count -gt 0 })) {
    foreach ($repo in $repos) {
        if ($c.SelfRepos.Contains($repo.Name)) { continue }
        $refs = Get-RepoRefs $repo.Name
        $hit = @($c.ProjNames | Where-Object { $refs.Contains($_) })
        if ($hit.Count -gt 0) { Add-Ev $c $repo.Name '' 0 'LibraryReference' 'Low' 'ProjectRef' '' ("References " + ($hit -join ',')) '' ($hit -join ',') }
    }
}

# --- host-in-repo boost: a repo that mentions the owner's host name AND matches the route is much more likely a real consumer
$boosted = 0
foreach ($e in $ev) {
    if ($e.Confidence -eq 'High' -or $e.IsSelf -or $e.Resolution -match 'HostMismatch') { continue }
    if ($e.EvidenceType -notin 'RouteLiteral', 'ServiceUrlReference') { continue }
    $c = $cl[$e.ClusterId]
    if ($c.Hosts.Count -eq 0 -or -not $hostSeen.ContainsKey($e.ConsumerRepo)) { continue }
    if (@($c.Hosts | Where-Object { $hostSeen[$e.ConsumerRepo].Contains($_) }).Count -gt 0) {
        $e.Confidence = $(if ($e.Resolution -like 'Prefix*') { 'Medium' } else { 'High' })   # prefix-only matches never exceed Medium
        $e.Resolution = $e.Resolution + '+HostInRepo'; $boosted++
    }
}

# ------------------------------------------------------------------------------------------------
# Outputs
# ------------------------------------------------------------------------------------------------
$evArr = $ev.ToArray()
$evArr | Sort-Object ClusterId, ConsumerRepo, File, Line | Export-Csv -Path (Join-Path $OutputPath 'usage-detail.csv') -NoTypeInformation -Encoding UTF8
if ($HostMapPath) { $hostRefs.ToArray() | Export-Csv -Path (Join-Path $OutputPath 'host-references.csv') -NoTypeInformation -Encoding UTF8 }

$summary = New-Object System.Collections.Generic.List[object]
foreach ($g in ($evArr | Group-Object ClusterId, ConsumerRepo)) {
    $rows = $g.Group
    $best = ($rows | Sort-Object { -$rank[$_.Confidence] } | Select-Object -First 1)
    $c = $cl[$best.ClusterId]
    $summary.Add([pscustomobject][ordered]@{
        ClusterId = $c.Id; Kind = $c.Kind; Endpoint = $c.Name; Owner = $c.Owner; ConsumerRepo = $best.ConsumerRepo; InHomeRepo = $best.IsSelf
        BestConfidence = $best.Confidence; EvidenceCount = $rows.Count
        EvidenceTypes = (($rows | Select-Object -ExpandProperty EvidenceType -Unique) -join ';')
        Resolutions = (($rows | Select-Object -ExpandProperty Resolution -Unique) -join ';')
        TargetHostRepos = (($rows | Where-Object TargetHostRepo | ForEach-Object { $_.TargetHostRepo -split ';' } | Sort-Object -Unique) -join ';')
        OperationsUsed = (($rows | Where-Object { $_.EvidenceType -eq 'OperationCall' } | Select-Object -ExpandProperty Operation -Unique | Sort-Object) -join ';')
        AmbiguousWith = (($rows | Where-Object Candidates | Select-Object -ExpandProperty Candidates -Unique) -join ' | ')
        BestFile = $best.File; BestLine = $best.Line; BestSnippet = $best.Snippet
    })
}
$summary.ToArray() | Sort-Object Endpoint, @{ e = { -$rank[$_.BestConfidence] } }, ConsumerRepo | Export-Csv -Path (Join-Path $OutputPath 'consumer-summary.csv') -NoTypeInformation -Encoding UTF8

$rollup = foreach ($c in $inScopeClusters) {
    $mine = @($summary | Where-Object { $_.ClusterId -eq $c.Id })
    $solid = @($mine | Where-Object { $_.BestConfidence -in 'High', 'Medium' })
    $weak  = @($mine | Where-Object { $_.BestConfidence -eq 'Low' })
    $used = @($mine | ForEach-Object { Split-List $_.OperationsUsed } | Sort-Object -Unique)
    $review = New-Object System.Collections.Generic.List[string]
    if ($solid.Count -eq 0) { $review.Add('NoStaticConsumerFound-checkRuntimeLogs') }
    if ($weak.Count -gt 0) { $review.Add('LowConfidenceHitsToReview') }
    if ($c.Flags -match 'NameCollision') { $review.Add('NameCollision') }
    if ($c.Flags -match 'DuplicateRoute|MultiHost|DefinedInMultipleRepos') { $review.Add('DuplicateDefinitions') }
    if ($c.HostRepoSet.Count -gt 1 -and $solid.Count -gt 0 -and @($mine | Where-Object { $_.TargetHostRepos }).Count -lt $solid.Count) { $review.Add('SomeConsumersNotTiedToAHostRepo(add -HostMapPath or check runtime logs)') }
    if ($c.Kind -eq 'REST' -and -not $c.Key) { $review.Add('RouteNotSearchable(startsWithParameter)') }
    [pscustomobject][ordered]@{
        ClusterId = $c.Id; Kind = $c.Kind; Endpoint = $c.Name; Owner = $c.Owner
        ConsumerCount_HighMedium = $solid.Count; ConsumerRepos_HighMedium = (($solid | Select-Object -ExpandProperty ConsumerRepo) -join ';')
        ConsumerRepos_LowOnly = (($weak | Select-Object -ExpandProperty ConsumerRepo) -join ';')
        HostRepos = (($c.HostRepoSet | Sort-Object) -join ';')
        ConsumersPerHostRepo = $(if ($c.HostRepoSet.Count -gt 1) { (($c.HostRepoSet | Sort-Object | ForEach-Object { $h = $_; $who = @($mine | Where-Object { (Split-List $_.TargetHostRepos) -contains $h } | ForEach-Object { $_.ConsumerRepo }); "$h=[" + ($who -join ',') + ']' }) -join ' ') } else { '' })
        KnownProxyRepos = (($c.ProxyRepos | Sort-Object) -join ';')
        OperationsSeenUsed = ($used -join ';')
        OperationsNotSeen = $(if ($c.Kind -eq 'WCF') { (@($c.Ops | Where-Object { $used -notcontains $_ }) -join ';') } else { '' })
        Flags = $c.Flags; ReviewHints = ($review -join ';')
    }
}
@($rollup) | Sort-Object Kind, Endpoint | Export-Csv -Path (Join-Path $OutputPath 'cluster-usage-rollup.csv') -NoTypeInformation -Encoding UTF8

Write-Host ''
Write-Host '==== Usage summary ====' -ForegroundColor Cyan
Write-Host ("Endpoints analysed         : {0}" -f $inScopeClusters.Count)
Write-Host ("Evidence rows              : {0}  (host-in-repo boosts: {1})" -f $evArr.Count, $boosted)
Write-Host ("Endpoint x consumer pairs  : {0}  (High/Medium: {1}, Low only: {2})" -f $summary.Count, @($summary | Where-Object { $_.BestConfidence -ne 'Low' }).Count, @($summary | Where-Object { $_.BestConfidence -eq 'Low' }).Count)
$none = @($rollup | Where-Object { $_.ConsumerCount_HighMedium -eq 0 })
if ($none.Count) { Write-Host ("Endpoints with no static consumer: {0} -> confirm with runtime logs before decommissioning" -f $none.Count) -ForegroundColor Yellow }
if ($notSearchable.Count) { Write-Host ("REST routes not searchable (start with a parameter): {0}" -f $notSearchable.Count) -ForegroundColor Yellow }
Write-Host "Output folder              : $OutputPath"
