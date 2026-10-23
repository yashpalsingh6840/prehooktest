<#
.SYNOPSIS
    STEP 1 - Scan a folder of cloned repos and build a de-duplicated inventory of WCF and REST endpoints.

.DESCRIPTION
    Finds, across every repo under -RootPath:
      * WCF   : [ServiceContract] interfaces + [OperationContract] operations (incl. WebGet/WebInvoke UriTemplates)
      * REST  : ASP.NET Core / Web API 2 controllers (attribute + simple convention routes) and Minimal API MapGet/MapPost/...

    The same interface name (e.g. IOrderService) often appears in many repos for very different reasons:
      - the real service contract (hand-written)      -> Host / Implementation / ContractOnly
      - a generated client proxy in a consumer repo   -> ProxyClient   (this is a CONSUMER, not a second endpoint!)
      - a copy/paste of the contract                  -> same cluster, flagged DefinedInMultipleRepos
      - an unrelated service that happens to share the name -> separate cluster, flagged NameCollision

    To keep those apart every contract gets a *wire identity* (SOAP namespace + contract name) and an operation
    fingerprint, and all occurrences are merged into ONE cluster (ClusterId). Ownership is decided by hosting
    evidence (.svc / <service> config / ServiceHost / CoreWCF / implementing class), not by who declares the interface.

.OUTPUTS  (in -OutputPath)
    endpoint-clusters.csv    One row per unique endpoint (WCF contract / REST route). THE list to migrate. Input to step 2.
    endpoint-inventory.csv   One row per occurrence x operation (every repo that declares/hosts/proxies it).
    repo-references.csv      Package / project / assembly references per repo (used to resolve shared contract libraries).
    repos.csv                The de-duplicated repo list that was scanned (step 2 re-uses it so repo names match).
    scan-warnings.csv        Unresolved or ambiguous items to review manually.

.EXAMPLE
    .\Get-EndpointInventory.ps1 -RootPath D:\repos -OutputPath D:\scan\out
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RootPath,
    [string]$OutputPath = (Join-Path (Get-Location) 'scan-output'),
    [string[]]$ExcludeRepo = @(),
    [string[]]$ExcludeDir = @('bin', 'obj', 'node_modules', '.git', '.vs', '.idea', 'packages', 'TestResults', 'artifacts'),
    # Two contract variants with the same wire identity are merged into one cluster when their operation sets overlap at least this much.
    [double]$DriftThreshold = 0.5,
    [switch]$IncludeTests,
    [switch]$SkipGit
)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'EndpointScan.Common.ps1')
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

$warnings = New-Object System.Collections.Generic.List[object]
function Add-Warn($repo, $file, $msg) { $warnings.Add([pscustomobject]@{ Repo = $repo; File = $file; Message = $msg }) }

#region regexes (applied to the *masked* text so brackets/braces inside strings never confuse them)
$RxIface   = New-Object Regex('(?<attrs>(?:\[[^\[\]]*\]\s*)+)(?:(?:public|internal)\s+)?(?:partial\s+)?interface\s+(?<name>\w+)', 'Compiled')
$RxOp      = New-Object Regex('(?<attrs>(?:\[[^\[\]]*\]\s*)+)(?<ret>(?:[^;{}()\[\]]|\[,*\])+?)\s+(?<m>\w+)\s*(?:<[^>]*>)?\s*\(', 'Compiled')
$RxNs      = New-Object Regex('\bnamespace\s+(?<n>[\w\.]+)', 'Compiled')
$RxClass   = New-Object Regex('(?<attrs>(?:\[[^\[\]]*\]\s*)*)(?<mods>(?:(?:public|internal|protected|private|abstract|sealed|partial|static)\s+)*)class\s+(?<name>\w+)\s*(?:<[^>{]*>)?\s*(?:\([^)]*\))?\s*(?::\s*(?<bases>[^{;]+?))?\s*\{', 'Compiled')
$RxMethod  = New-Object Regex('(?<attrs>(?:\[[^\[\]]*\]\s*)*)public\s+(?:(?:virtual|async|override|static|new)\s+)*(?<ret>(?:[^;{}()=\[\]]|\[,*\])+?)\s+(?<m>\w+)\s*(?:<[^>]*>)?\s*\(', 'Compiled')
$RxClientBase = New-Object Regex('(?:Duplex)?ClientBase\s*<\s*(?<i>[\w\.]+)\s*(?:,[^>]*)?>', 'Compiled')
$RxRestHint = New-Object Regex('Controller|\bMap(?:Get|Post|Put|Delete|Patch|Methods|Group)\s*\(', 'Compiled')
$RxHostCode = New-Object Regex('new\s+\w*ServiceHost\w*\s*\(\s*typeof\s*\(\s*(?<t>[\w\.]+)|AddService\s*<\s*(?<t2>[\w\.]+)|AddServiceEndpoint\s*<\s*(?<t3>[\w\.]+)\s*,\s*(?<c3>[\w\.]+)', 'Compiled')
#endregion

#region small helpers
function Get-ClrNs($nsList, [int]$idx) {
    $r = ''
    foreach ($n in $nsList) { if ($n.Idx -lt $idx) { $r = $n.Name } else { break } }
    return $r
}

function Read-Proj([string]$path) {
    try { $x = [xml][System.IO.File]::ReadAllText($path) } catch { return $null }
    $q = { param($n) @($x.SelectNodes("//*[local-name()='$n']")) }
    $name = [System.IO.Path]::GetFileNameWithoutExtension($path)
    $asm  = (& $q 'AssemblyName' | Select-Object -First 1)
    $pid_ = (& $q 'PackageId' | Select-Object -First 1)
    $asmName = if ($asm -and $asm.InnerText) { $asm.InnerText } else { $name }
    $pkgId   = if ($pid_ -and $pid_.InnerText) { $pid_.InnerText } else { $asmName }
    $pkgs  = @(& $q 'PackageReference' | ForEach-Object { $_.GetAttribute('Include') } | Where-Object { $_ })
    $prefs = @(& $q 'ProjectReference' | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension(($_.GetAttribute('Include') -replace '\\', '/')) } | Where-Object { $_ })
    $arefs = @(& $q 'Reference' | ForEach-Object { ($_.GetAttribute('Include') -split ',')[0].Trim() } | Where-Object { $_ })
    $raw = [System.IO.File]::ReadAllText($path)
    $isWeb = ($raw -match 'Microsoft\.NET\.Sdk\.Web') -or ($raw -match 'Microsoft\.AspNet\.WebApi|Microsoft\.AspNetCore|349C5851-65DF-11DA-9384-00065B846F21') -or
             (Test-Path -LiteralPath ([System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName($path), 'Global.asax')))
    $names = New-CiSet
    foreach ($n in @($name, $asmName, $pkgId)) { [void]$names.Add($n) }
    [pscustomobject]@{ Name = $name; AssemblyName = $asmName; PackageId = $pkgId; Names = $names; Packages = $pkgs; ProjRefs = $prefs; AsmRefs = $arefs; IsWeb = $isWeb }
}

function Find-Project($repo, [string]$file) {
    $d = [System.IO.Path]::GetDirectoryName($file)
    while ($d -and $d.Length -ge $repo.Dir.Length) {
        $k = $d.ToLowerInvariant()
        if ($repo.Projects.ContainsKey($k)) { return $repo.Projects[$k] }
        $d = [System.IO.Path]::GetDirectoryName($d)
    }
    return $null
}

function Join-Route($prefix, $tpl, [string]$ctl, [string]$action, [bool]$isWebApi2) {
    $flag = ''
    if ($null -ne $tpl -and ($tpl.StartsWith('/') -or $tpl.StartsWith('~/'))) {
        $r = $tpl.TrimStart('~', '/')
    } else {
        $parts = @($prefix, $tpl) | Where-Object { -not [string]::IsNullOrEmpty($_) }
        $r = (@($parts) | ForEach-Object { $_.Trim('/') }) -join '/'
        if ($null -eq $prefix -and $null -eq $tpl) {
            $r = if ($isWebApi2) { "api/$ctl" } else { "$ctl/$action" }
            $flag = 'ConventionRoute'
        }
    }
    $r = $r -replace '\[controller\]', $ctl -replace '\[action\]', $action
    [pscustomobject]@{ Route = $r; Flag = $flag }
}
#endregion

#region REST parsers
function Get-ControllerRoutes($text, $clean, $mask, $rel, $repoName, $proj, $nsList) {
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($cm in $RxClass.Matches($mask)) {
        $name = $cm.Groups['name'].Value
        if ($cm.Groups['mods'].Value -match '\babstract\b') { continue }
        $attrs = Get-Val $cm 'attrs' $clean
        $bases = $cm.Groups['bases'].Value
        $isCtl = ($attrs -match '\bApiController\b') -or ($bases -match '\b(Controller|ControllerBase|ApiController)\b') -or ($name -match 'Controller$')
        if (-not $isCtl) { continue }
        $isWebApi2 = [bool]($bases -match '\bApiController\b')
        $ctl = $name -replace 'Controller$', ''
        $classRoutes = @([regex]::Matches($attrs, '\b(?:Route|RoutePrefix)(?:Attribute)?\s*\(\s*"(?<t>[^"]*)"') | ForEach-Object { $_.Groups['t'].Value })
        $open = $cm.Index + $cm.Length - 1
        $close = [CsMasker]::MatchBrace($mask, $open)
        $bodyMask = $mask.Substring($open, $close - $open + 1)
        $bodyClean = $clean.Substring($open, $close - $open + 1)
        $clrNs = Get-ClrNs $nsList $cm.Index
        foreach ($mm in $RxMethod.Matches($bodyMask)) {
            $mattrs = Get-Val $mm 'attrs' $bodyClean
            if ($mattrs -match '\bNonAction\b') { continue }
            $mname = $mm.Groups['m'].Value
            $verbs = @(); $tpl = $null
            foreach ($vm in [regex]::Matches($mattrs, '\bHttp(Get|Post|Put|Delete|Patch|Head|Options)(?:Attribute)?\b(?:\s*\(\s*(?:template\s*:\s*)?"(?<t>[^"]*)")?')) {
                $verbs += $vm.Groups[1].Value.ToUpperInvariant()
                if ($vm.Groups['t'].Success -and $null -eq $tpl) { $tpl = $vm.Groups['t'].Value }
            }
            $rm = [regex]::Match($mattrs, '\bRoute(?:Attribute)?\s*\(\s*"(?<t>[^"]*)"')
            if ($rm.Success -and $null -eq $tpl) { $tpl = $rm.Groups['t'].Value }
            $conv = $false
            if ($verbs.Count -eq 0) {
                $av = [regex]::Match($mattrs, '\bAcceptVerbs\s*\((?<a>[^)]*)\)')
                if ($av.Success) { $verbs = @([regex]::Matches($av.Groups['a'].Value, '"(\w+)"') | ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() }) }
            }
            if ($verbs.Count -eq 0 -and $isWebApi2 -and $mname -match '^(Get|Post|Put|Delete|Patch|Head|Options)') { $verbs = @($Matches[1].ToUpperInvariant()); $conv = $true }
            if ($verbs.Count -eq 0 -and $rm.Success) { $verbs = @('ANY') }
            if ($verbs.Count -eq 0) { continue }
            $line = [CsMasker]::Line($text, $open + $mm.Index)
            $prefixes = if ($classRoutes.Count -gt 0) { $classRoutes } else { @($null) }
            foreach ($verb in ($verbs | Select-Object -Unique)) {
                foreach ($prefix in $prefixes) {
                    $jr = Join-Route $prefix $tpl $ctl $mname $isWebApi2
                    $flag = $jr.Flag
                    if ($conv -and -not $flag) { $flag = 'ConventionVerb' }
                    $out.Add([pscustomobject]@{
                        Repo = $repoName; Rel = $rel; Line = $line; Controller = $name; ClrNs = $clrNs; Method = $mname
                        Verb = $verb; Route = $jr.Route; RouteNorm = (Normalize-Route $jr.Route)
                        Framework = $(if ($isWebApi2) { 'WebApi2' } else { 'AspNetCore' })
                        Project = $(if ($proj) { $proj.Name } else { '' }); IsWeb = $(if ($proj) { [bool]$proj.IsWeb } else { $false }); Flag = $flag
                    })
                }
            }
        }
    }
    return $out
}

function Get-MinimalRoutes($text, $clean, $rel, $repoName, $proj) {
    $out = New-Object System.Collections.Generic.List[object]
    $groups = @{}
    foreach ($gm in [regex]::Matches($clean, '(?<g>\w+)\s*=\s*(?<p>\w+)\s*\.\s*MapGroup\s*\(\s*"(?<t>[^"]*)"')) {
        $groups[$gm.Groups['g'].Value] = @{ Parent = $gm.Groups['p'].Value; T = $gm.Groups['t'].Value }
    }
    function Resolve-GroupPrefix([string]$v, [int]$depth) {
        if (-not $groups.ContainsKey($v) -or $depth -gt 5) { return '' }
        $g = $groups[$v]
        return ((Resolve-GroupPrefix $g.Parent ($depth + 1)) + '/' + $g.T.Trim('/'))
    }
    $emit = {
        param($idx, $verb, $route, $flag)
        $r = ([regex]::Replace($route, '/{2,}', '/')).Trim('/')
        $out.Add([pscustomobject]@{
            Repo = $repoName; Rel = $rel; Line = [CsMasker]::Line($text, $idx); Controller = '(MinimalApi)'; ClrNs = ''; Method = "Map$verb"
            Verb = $verb.ToUpperInvariant(); Route = $r; RouteNorm = (Normalize-Route $r); Framework = 'MinimalApi'
            Project = $(if ($proj) { $proj.Name } else { '' }); IsWeb = $(if ($proj) { [bool]$proj.IsWeb } else { $false }); Flag = $flag
        })
    }
    foreach ($rm in [regex]::Matches($clean, '(?<v>\w+)\s*\.\s*Map(?<verb>Get|Post|Put|Delete|Patch)\s*\(\s*@?"(?<t>[^"]*)"')) {
        $prefix = Resolve-GroupPrefix $rm.Groups['v'].Value 0
        & $emit $rm.Index $rm.Groups['verb'].Value (($prefix + '/' + $rm.Groups['t'].Value)) ''
    }
    foreach ($rm in [regex]::Matches($clean, 'MapGroup\s*\(\s*"(?<g>[^"]*)"\s*\)\s*\.\s*Map(?<verb>Get|Post|Put|Delete|Patch)\s*\(\s*@?"(?<t>[^"]*)"')) {
        & $emit $rm.Index $rm.Groups['verb'].Value (($rm.Groups['g'].Value + '/' + $rm.Groups['t'].Value)) 'GroupChain'
    }
    return $out
}
#endregion

# ------------------------------------------------------------------------------------------------
# PHASE 0 - discover repos (and skip duplicate clones of the same remote)
# ------------------------------------------------------------------------------------------------
$repoDirs = @(Find-Repos -Root $RootPath -ExcludeDir $ExcludeDir -ExcludeRepo $ExcludeRepo)
Write-Host "Found $($repoDirs.Count) candidate repo folder(s) under $RootPath"
$repos = [ordered]@{}
$seenRemote = @{}
foreach ($dir in $repoDirs) {
    $meta = Get-RepoMeta -Dir $dir -SkipGit:$SkipGit
    if ($meta.RemoteNorm -and $seenRemote.ContainsKey($meta.RemoteNorm)) {
        Add-Warn $meta.Name '' "Skipped: same git remote as '$($seenRemote[$meta.RemoteNorm])' (duplicate clone/worktree)"
        continue
    }
    if ($meta.RemoteNorm) { $seenRemote[$meta.RemoteNorm] = $meta.Name }
    if ($repos.Contains($meta.Name)) { $meta.Name = $meta.Name + '~' + (Get-ShortHash $dir).Substring(0, 4); Add-Warn $meta.Name '' 'Repo folder name not unique; suffix added' }
    $meta | Add-Member -NotePropertyName HostedTypes     -NotePropertyValue (New-CiSet)
    $meta | Add-Member -NotePropertyName HostedContracts -NotePropertyValue (New-CiSet)
    $meta | Add-Member -NotePropertyName SvcFiles        -NotePropertyValue @{}
    $meta | Add-Member -NotePropertyName Refs            -NotePropertyValue (New-CiSet)
    $meta | Add-Member -NotePropertyName CsFiles         -NotePropertyValue (New-Object System.Collections.Generic.List[string])
    $meta | Add-Member -NotePropertyName Projects        -NotePropertyValue @{}
    $repos[$meta.Name] = $meta
}

# ------------------------------------------------------------------------------------------------
# PHASE 1 - per-repo scan: projects, hosting evidence (.svc / config), contracts, controllers
# ------------------------------------------------------------------------------------------------
$contractOccs = New-Object System.Collections.Generic.List[object]
$routeRows    = New-Object System.Collections.Generic.List[object]
$repoNo = 0
foreach ($repo in $repos.Values) {
    $repoNo++
    Write-Progress -Activity 'Pass 1: contracts, controllers, hosting evidence' -Status "$($repo.Name) ($repoNo/$($repos.Count))" -PercentComplete (100 * $repoNo / $repos.Count)
    $files = Get-RepoFiles -Dir $repo.Dir -ExcludeDir $ExcludeDir
    $rootLen = $repo.Dir.Length

    # --- projects (needed first so every file can be mapped to its project / package / assembly)
    foreach ($f in $files) {
        if ($f -match '\.(cs|vb|fs)proj$') {
            $p = Read-Proj $f
            if ($null -eq $p) { Add-Warn $repo.Name $f 'Could not parse project file'; continue }
            $k = ([System.IO.Path]::GetDirectoryName($f)).ToLowerInvariant()
            if (-not $repo.Projects.ContainsKey($k)) { $repo.Projects[$k] = $p }
            foreach ($x in @($p.Packages + $p.ProjRefs + $p.AsmRefs)) { if ($x) { [void]$repo.Refs.Add($x) } }
        }
    }

    # --- hosting evidence: .svc files and <system.serviceModel> config
    foreach ($f in $files) {
        $rel = $f.Substring($rootLen).TrimStart('\', '/')
        if ($f -match '\.svc$') {
            $t = Read-Text $f; if (-not $t) { continue }
            $m = [regex]::Match($t, 'Service\s*=\s*"([^"]+)"')
            if ($m.Success) {
                $short = Get-ShortName $m.Groups[1].Value
                [void]$repo.HostedTypes.Add($short)
                if (-not $repo.SvcFiles.ContainsKey($short)) { $repo.SvcFiles[$short] = New-Object System.Collections.Generic.List[string] }
                $repo.SvcFiles[$short].Add(($rel -replace '\\', '/'))
            }
        }
        elseif ($f -match '\.config$' -and $f -notmatch '(?i)[\\/](packages|nuget)\.config$') {
            $t = Read-Text $f; if (-not $t -or -not $t.Contains('serviceModel')) { continue }
            try { $doc = [xml]$t } catch { Add-Warn $repo.Name $rel 'Config not parseable as XML'; continue }
            foreach ($svc in @($doc.SelectNodes('//system.serviceModel/services/service'))) {
                $short = Get-ShortName $svc.GetAttribute('name')
                if ($short) { [void]$repo.HostedTypes.Add($short) }
                foreach ($ep in @($svc.SelectNodes('endpoint'))) {
                    $c = Get-ShortName $ep.GetAttribute('contract')
                    if ($c -and $c -ne 'IMetadataExchange') { [void]$repo.HostedContracts.Add($c) }
                }
            }
            foreach ($add in @($doc.SelectNodes('//system.serviceModel/serviceHostingEnvironment/serviceActivations/add'))) {
                $short = Get-ShortName $add.GetAttribute('service')
                if ($short) {
                    [void]$repo.HostedTypes.Add($short)
                    if (-not $repo.SvcFiles.ContainsKey($short)) { $repo.SvcFiles[$short] = New-Object System.Collections.Generic.List[string] }
                    $repo.SvcFiles[$short].Add($add.GetAttribute('relativeAddress'))
                }
            }
        }
    }

    # --- C# files
    foreach ($f in $files) {
        if ($f -notmatch '\.cs$' -or $f -match '\.(g|g\.i|designer|AssemblyInfo)\.cs$') { continue }
        $rel = $f.Substring($rootLen).TrimStart('\', '/')
        if (-not $IncludeTests -and (Test-IsTestPath $rel)) { continue }
        $repo.CsFiles.Add($f)
        $text = Read-Text $f
        if (-not $text -or $text.Length -lt 30) { continue }
        $needSvc  = $text.Contains('ServiceContract')
        $needRest = $RxRestHint.IsMatch($text)
        $needHost = $text.Contains('ServiceHost') -or $text.Contains('AddService<') -or $text.Contains('AddServiceEndpoint<')
        if (-not ($needSvc -or $needRest -or $needHost)) { continue }

        try {
            $mm = [CsMasker]::Process($text); $clean = $mm[0]; $mask = $mm[1]
            $nsList = @($RxNs.Matches($mask) | ForEach-Object { [pscustomobject]@{ Idx = $_.Index; Name = $_.Groups['n'].Value } })
            $proj = Find-Project $repo $f

            if ($needHost) {
                foreach ($hm in $RxHostCode.Matches($clean)) {
                    foreach ($g in 't', 't2', 't3') { if ($hm.Groups[$g].Success) { [void]$repo.HostedTypes.Add((Get-ShortName $hm.Groups[$g].Value)) } }
                    if ($hm.Groups['c3'].Success) { [void]$repo.HostedContracts.Add((Get-ShortName $hm.Groups['c3'].Value)) }
                }
            }

            if ($needSvc) {
                $isGen = ($rel -match '(?i)(Connected Services|Service References|ServiceReference)') -or
                         ($clean -match 'GeneratedCode(?:Attribute)?\s*\(\s*"(?:System\.ServiceModel|dotnet-svcutil|svcutil)')
                foreach ($im in $RxIface.Matches($mask)) {
                    $attrs = Get-Val $im 'attrs' $clean
                    if ($attrs -notmatch 'ServiceContract') { continue }
                    $iface = $im.Groups['name'].Value
                    $sc = [regex]::Match($attrs, 'ServiceContract(?:Attribute)?\s*(?:\((?<a>[^)]*)\))?')
                    $args_ = $sc.Groups['a'].Value
                    $wireNs   = [regex]::Match($args_, '(?<![\w])Namespace\s*=\s*"([^"]*)"').Groups[1].Value
                    $wireName = [regex]::Match($args_, '(?<![\w])Name\s*=\s*"([^"]*)"').Groups[1].Value
                    $cfgName  = [regex]::Match($args_, 'ConfigurationName\s*=\s*"([^"]*)"').Groups[1].Value
                    if (-not $wireNs)   { $wireNs = 'http://tempuri.org/' }
                    if (-not $wireName) { $wireName = $iface }

                    $open = $mask.IndexOf('{', $im.Index + $im.Length)
                    if ($open -lt 0) { continue }
                    $close = [CsMasker]::MatchBrace($mask, $open)
                    $bodyMask = $mask.Substring($open, $close - $open + 1)
                    $bodyClean = $clean.Substring($open, $close - $open + 1)

                    $ops = New-Object System.Collections.Generic.List[object]
                    foreach ($om in $RxOp.Matches($bodyMask)) {
                        $oattrs = Get-Val $om 'attrs' $bodyClean
                        if ($oattrs -notmatch '\bOperationContract(?:Attribute)?\b') { continue }
                        if ($oattrs -match 'AsyncPattern\s*=\s*true') { continue }   # Begin/End pair - the sync/Task form is the real operation
                        $meth = $om.Groups['m'].Value
                        $explicit = [regex]::Match($oattrs, '(?<![\w])Name\s*=\s*"([^"]*)"').Groups[1].Value
                        $opName = if ($explicit) { $explicit } elseif ($meth.Length -gt 5 -and $meth.EndsWith('Async')) { $meth.Substring(0, $meth.Length - 5) } else { $meth }
                        $action = [regex]::Match($oattrs, '(?<![\w])Action\s*=\s*"([^"]*)"').Groups[1].Value
                        if (-not $action) { $action = ($wireNs.TrimEnd('/') + '/' + $wireName + '/' + $opName) }
                        $uri = [regex]::Match($oattrs, 'UriTemplate\s*=\s*"([^"]*)"').Groups[1].Value
                        $verb = ''
                        if ($oattrs -match '\bWebGet\b') { $verb = 'GET' }
                        elseif ($oattrs -match '\bWebInvoke\b') { $vm = [regex]::Match($oattrs, 'Method\s*=\s*"(\w+)"'); $verb = if ($vm.Success) { $vm.Groups[1].Value.ToUpperInvariant() } else { 'POST' } }
                        if ($ops | Where-Object { $_.Name -eq $opName }) { continue }   # sync + Async twin of the same wire operation
                        $ops.Add([pscustomobject]@{ Name = $opName; Method = $meth; Action = $action; Uri = $uri; Verb = $verb; Line = [CsMasker]::Line($text, $open + $om.Index) })
                    }
                    $opNames = @($ops | Select-Object -ExpandProperty Name | Sort-Object -Unique)
                    $contractOccs.Add([pscustomobject]@{
                        Repo = $repo.Name; Rel = ($rel -replace '\\', '/'); Line = [CsMasker]::Line($text, $im.Index); Iface = $iface
                        ClrNs = (Get-ClrNs $nsList $im.Index); WireNs = $wireNs; WireName = $wireName; ConfigName = $cfgName
                        WireKey = ($wireNs.TrimEnd('/').ToLowerInvariant() + '|' + $wireName.ToLowerInvariant())
                        Ops = $opNames; OpKey = (($opNames | ForEach-Object { $_.ToLowerInvariant() }) -join ','); OpDetails = $ops.ToArray()
                        Project = $(if ($proj) { $proj.Name } else { '' }); ProjectNames = $(if ($proj) { @($proj.Names) } else { @() })
                        IsGen = [bool]$isGen; Role = $(if ($isGen) { 'ProxyClient' } else { 'ContractOnly' }); Resolution = 'Declared'
                        Confidence = 'High'; IsPseudo = $false; ImplClasses = (New-Object System.Collections.Generic.List[string]); ClusterId = ''; IsDrifted = $false
                    })
                }
            }

            if ($needRest) {
                foreach ($r in (Get-ControllerRoutes $text $clean $mask ($rel -replace '\\', '/') $repo.Name $proj $nsList)) { $routeRows.Add($r) }
                if ($clean -match '\bMap(?:Get|Post|Put|Delete|Patch)\s*\(') {
                    foreach ($r in (Get-MinimalRoutes $text $clean ($rel -replace '\\', '/') $repo.Name $proj)) { $routeRows.Add($r) }
                }
            }
        } catch {
            Add-Warn $repo.Name $rel ("Parse error: " + $_.Exception.Message)
        }
    }
}
Write-Progress -Activity 'Pass 1: contracts, controllers, hosting evidence' -Completed
Write-Host ("Pass 1 done: {0} contract declarations, {1} REST routes" -f $contractOccs.Count, $routeRows.Count)

# ------------------------------------------------------------------------------------------------
# PHASE 2 - cluster WCF contracts by WIRE IDENTITY (+ operation overlap), not by C# name
# ------------------------------------------------------------------------------------------------
$wcfClusters = New-Object System.Collections.Generic.List[object]
foreach ($g in ($contractOccs | Group-Object WireKey)) {
    $variants = @($g.Group | Group-Object OpKey | ForEach-Object {
        [pscustomobject]@{ OpKey = $_.Name; Ops = $_.Group[0].Ops; Occ = $_.Group; HasHand = [int]([bool]($_.Group | Where-Object { -not $_.IsGen })) }
    }) | Sort-Object @{ e = { -$_.HasHand } }, @{ e = { -@($_.Ops).Count } }, OpKey
    $local = New-Object System.Collections.Generic.List[object]
    foreach ($v in $variants) {
        $target = $null
        foreach ($c in $local) { if ((Get-Jaccard $v.Ops $c.SeedOps) -ge $DriftThreshold) { $target = $c; break } }
        if (-not $target) {
            $first = $v.Occ[0]
            $target = [pscustomobject]@{
                Id = 'WCF-' + (Get-ShortHash ($g.Name + '|' + $v.OpKey)); Kind = 'WCF'; SeedOpKey = $v.OpKey; SeedOps = $v.Ops
                WireKey = $g.Name; WireNs = $first.WireNs; Name = $first.WireName
                Occs = (New-Object System.Collections.Generic.List[object]); ClientClasses = (New-Object System.Collections.Generic.List[string])
            }
            $local.Add($target); $wcfClusters.Add($target)
        }
        foreach ($o in $v.Occ) { $o.ClusterId = $target.Id; $o.IsDrifted = ($v.OpKey -ne $target.SeedOpKey); $target.Occs.Add($o) }
    }
}
$clustersByIface = @{}
foreach ($c in $wcfClusters) {
    foreach ($i in ($c.Occs | Select-Object -ExpandProperty Iface -Unique)) {
        $k = $i.ToLowerInvariant()
        if (-not $clustersByIface.ContainsKey($k)) { $clustersByIface[$k] = New-Object System.Collections.Generic.List[object] }
        if (-not $clustersByIface[$k].Contains($c)) { $clustersByIface[$k].Add($c) }
    }
}

# Resolve "which cluster does interface name N mean inside repo R?"  local declaration > project/package reference > unique name > ambiguous
function Resolve-Cluster([string]$repo, [string]$iface) {
    $cands = $clustersByIface[$iface.ToLowerInvariant()]
    if (-not $cands) { return $null }
    $loc = @($cands | Where-Object { $c = $_; @($c.Occs | Where-Object { $_.Repo -eq $repo -and $_.Iface -eq $iface -and -not $_.IsPseudo }).Count -gt 0 })
    if ($loc.Count -gt 0) { return [pscustomobject]@{ Clusters = $loc; Method = 'Local'; Confidence = 'High' } }
    $refs = $repos[$repo].Refs
    $byRef = @($cands | Where-Object { $c = $_; @($c.Occs | Where-Object { -not $_.IsGen -and -not $_.IsPseudo -and ($_.ProjectNames | Where-Object { $refs.Contains($_) }) }).Count -gt 0 })
    if ($byRef.Count -gt 0) { return [pscustomobject]@{ Clusters = $byRef; Method = 'ProjectRef'; Confidence = 'High' } }
    if ($cands.Count -eq 1) { return [pscustomobject]@{ Clusters = $cands.ToArray(); Method = 'NameOnly'; Confidence = 'Low' } }
    return [pscustomobject]@{ Clusters = $cands.ToArray(); Method = 'Ambiguous'; Confidence = 'Low' }
}

# ------------------------------------------------------------------------------------------------
# PHASE 3 - pass 2: find implementations and manual client proxies of the contracts we found
# ------------------------------------------------------------------------------------------------
$ifaceSet = New-CiSet
foreach ($o in $contractOccs) { [void]$ifaceSet.Add($o.Iface) }
if ($ifaceSet.Count -gt 0) {
    $nameRx = New-Object Regex(('\b(?:' + (($ifaceSet | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'), 'Compiled')
    $repoNo = 0
    foreach ($repo in $repos.Values) {
        $repoNo++
        Write-Progress -Activity 'Pass 2: implementations & client proxies' -Status "$($repo.Name) ($repoNo/$($repos.Count))" -PercentComplete (100 * $repoNo / $repos.Count)
        foreach ($f in $repo.CsFiles) {
            $text = Read-Text $f
            if (-not $text -or -not $nameRx.IsMatch($text)) { continue }
            $rel = ($f.Substring($repo.Dir.Length).TrimStart('\', '/')) -replace '\\', '/'
            try {
                $mm = [CsMasker]::Process($text); $mask = $mm[1]
                $nsList = @($RxNs.Matches($mask) | ForEach-Object { [pscustomobject]@{ Idx = $_.Index; Name = $_.Groups['n'].Value } })
                foreach ($cm in $RxClass.Matches($mask)) {
                    $bases = $cm.Groups['bases'].Value
                    if (-not $bases) { continue }
                    $cls = $cm.Groups['name'].Value
                    $line = [CsMasker]::Line($text, $cm.Index)
                    foreach ($pm in $RxClientBase.Matches($bases)) {
                        $ifc = Get-ShortName $pm.Groups['i'].Value
                        if ($ifaceSet.Contains($ifc)) {
                            $res = Resolve-Cluster $repo.Name $ifc
                            if (-not $res -or $res.Method -eq 'Ambiguous') { Add-Warn $repo.Name $rel "Client proxy '$cls' for '$ifc' could not be tied to a single contract (ambiguous)"; continue }
                            foreach ($cl in $res.Clusters) {
                                $localGen = @($cl.Occs | Where-Object { $_.Repo -eq $repo.Name -and $_.IsGen -and $_.Iface -eq $ifc }).Count -gt 0
                                if ($localGen) { if (-not $cl.ClientClasses.Contains($cls)) { $cl.ClientClasses.Add($cls) } }
                                else {
                                    $cl.Occs.Add([pscustomobject]@{ Repo = $repo.Name; Rel = $rel; Line = $line; Iface = $ifc; ClrNs = (Get-ClrNs $nsList $cm.Index); WireNs = $cl.WireNs; WireName = $cl.Name
                                        ConfigName = ''; Ops = @(); OpDetails = @(); Project = ''; ProjectNames = @(); IsGen = $false; Role = 'ProxyClient'; Resolution = $res.Method
                                        Confidence = $res.Confidence; IsPseudo = $true; ImplClasses = (New-Object System.Collections.Generic.List[string]); ClusterId = $cl.Id; IsDrifted = $false })
                                    if (-not $cl.ClientClasses.Contains($cls)) { $cl.ClientClasses.Add($cls) }
                                }
                            }
                        }
                    }
                    # A class deriving from ClientBase<T> is a client proxy even though svcutil also lists T in its base list.
                    if ($RxClientBase.IsMatch($bases)) { continue }
                    foreach ($tm in [regex]::Matches($bases, '[\w\.]+')) {
                        $ifc = Get-ShortName $tm.Value
                        if (-not $ifaceSet.Contains($ifc)) { continue }
                        $res = Resolve-Cluster $repo.Name $ifc
                        if (-not $res) { continue }
                        if ($res.Method -eq 'Ambiguous') { Add-Warn $repo.Name $rel "Class '$cls' implements '$ifc' but several different contracts share that name and none is referenced by this repo"; continue }
                        $hosted = $repo.HostedTypes.Contains($cls) -or $repo.HostedContracts.Contains($ifc)
                        $newRole = if ($hosted) { 'Host' } else { 'Implementation' }
                        foreach ($cl in $res.Clusters) {
                            $own = @($cl.Occs | Where-Object { $_.Repo -eq $repo.Name -and $_.Iface -eq $ifc -and -not $_.IsGen -and -not $_.IsPseudo })
                            if ($own.Count -gt 0) {
                                foreach ($o in $own) {
                                    $o.ImplClasses.Add($cls)
                                    if ($newRole -eq 'Host' -or $o.Role -eq 'ContractOnly') { $o.Role = $newRole }
                                }
                            } else {
                                $cl.Occs.Add([pscustomobject]@{ Repo = $repo.Name; Rel = $rel; Line = $line; Iface = $ifc; ClrNs = (Get-ClrNs $nsList $cm.Index); WireNs = $cl.WireNs; WireName = $cl.Name
                                    ConfigName = ''; Ops = @(); OpDetails = @(); Project = ''; ProjectNames = @(); IsGen = $false; Role = $newRole; Resolution = $res.Method
                                    Confidence = $res.Confidence; IsPseudo = $true; ImplClasses = (New-Object System.Collections.Generic.List[string]); ClusterId = $cl.Id; IsDrifted = $false })
                                $cl.Occs[$cl.Occs.Count - 1].ImplClasses.Add($cls)
                            }
                        }
                    }
                }
            } catch { Add-Warn $repo.Name $rel ("Pass-2 parse error: " + $_.Exception.Message) }
        }
    }
    Write-Progress -Activity 'Pass 2: implementations & client proxies' -Completed
}

# ------------------------------------------------------------------------------------------------
# PHASE 3b - hosted services whose contract was NOT found in any scanned repo (e.g. interface lives in a NuGet package
#            we have no source for). Create a host-only cluster from the .svc file so the service can still be searched for.
# ------------------------------------------------------------------------------------------------
foreach ($repo in $repos.Values) {
    foreach ($type in @($repo.SvcFiles.Keys)) {
        $covered = $false
        foreach ($c in $wcfClusters) { foreach ($o in $c.Occs) { if ($o.Repo -eq $repo.Name -and $o.ImplClasses.Contains($type)) { $covered = $true } } }
        if ($covered) { continue }
        # find the implementing class + its interface(s)
        $implFile = ''; $implLine = 1; $ifaces = New-Object System.Collections.Generic.List[string]
        $clsRx = New-Object Regex(('\bclass\s+' + [regex]::Escape($type) + '\b'))
        foreach ($f in $repo.CsFiles) {
            $t = Read-Text $f; if (-not $t -or -not $clsRx.IsMatch($t)) { continue }
            $m2 = [CsMasker]::Process($t)[1]
            foreach ($cm in $RxClass.Matches($m2)) {
                if ($cm.Groups['name'].Value -ne $type) { continue }
                $implFile = ($f.Substring($repo.Dir.Length).TrimStart('\', '/')) -replace '\\', '/'; $implLine = [CsMasker]::Line($t, $cm.Index)
                foreach ($tm in [regex]::Matches($cm.Groups['bases'].Value, '[\w\.]+')) { $n = Get-ShortName $tm.Value; if ($n -match '^I[A-Z]' -and -not $ifaces.Contains($n)) { $ifaces.Add($n) } }
            }
            if ($implFile) { break }
        }
        $ifaceName = if ($ifaces.Count -gt 0) { $ifaces[0] } else { $type }
        $id = 'WCF-' + (Get-ShortHash ('hostonly|' + $repo.Name + '|' + $type))
        $newC = [pscustomobject]@{
            Id = $id; Kind = 'WCF'; SeedOpKey = ''; SeedOps = @(); WireKey = ''; WireNs = ''; Name = $ifaceName
            Occs = (New-Object System.Collections.Generic.List[object]); ClientClasses = (New-Object System.Collections.Generic.List[string])
            ExtraFlags = (New-Object System.Collections.Generic.List[string])
        }
        $newC.ExtraFlags.Add('ContractNotInScannedRepos')
        $svcRel = $repo.SvcFiles[$type][0]
        $newC.Occs.Add([pscustomobject]@{ Repo = $repo.Name; Rel = $(if ($implFile) { $implFile } else { $svcRel }); Line = $implLine; Iface = $ifaceName; ClrNs = ''; WireNs = ''; WireName = $ifaceName
            ConfigName = ''; Ops = @(); OpDetails = @(); Project = ''; ProjectNames = @(); IsGen = $false; Role = 'Host'; Resolution = 'HostOnly'
            Confidence = 'Medium'; IsPseudo = $true; ImplClasses = (New-Object System.Collections.Generic.List[string]); ClusterId = $id; IsDrifted = $false })
        $newC.Occs[0].ImplClasses.Add($type)
        # if the consumers' generated proxies reveal the contract (wire namespace + operations), adopt them - but only when unambiguous
        $proxyOnly = @($wcfClusters | Where-Object {
            $cc = $_
            (@($cc.Occs | Where-Object { -not $_.IsGen -and -not $_.IsPseudo }).Count -eq 0) -and (@($cc.Occs | Where-Object { $ifaces -contains $_.Iface -or $_.Iface -eq $ifaceName }).Count -gt 0) })
        if ($proxyOnly.Count -eq 1) {
            $pc = $proxyOnly[0]
            foreach ($o in $pc.Occs) { $o.ClusterId = $id; $newC.Occs.Add($o) }
            foreach ($x in $pc.ClientClasses) { $newC.ClientClasses.Add($x) }
            $newC.WireNs = $pc.WireNs; $newC.Name = $pc.Name
            $newC.ExtraFlags.Add('ProxiesMergedByInterfaceName')
            [void]$wcfClusters.Remove($pc)
        } elseif ($proxyOnly.Count -gt 1) { $newC.ExtraFlags.Add('SeveralProxyContractsShareThisName') }
        $wcfClusters.Add($newC)
        Add-Warn $repo.Name $svcRel "Service '$type' is hosted here but its contract '$ifaceName' was not found in any scanned repo (cluster $id is host-only)"
    }
}

# ------------------------------------------------------------------------------------------------
# PHASE 4 - ownership, flags, REST clustering, output
# ------------------------------------------------------------------------------------------------
function Select-Owner($repoNames) {
    $best = $repoNames | Sort-Object { - $repos[$_].LastCommit.Ticks }, { $_ } | Select-Object -First 1
    return $best
}
$clusterRows = New-Object System.Collections.Generic.List[object]
$invRows = New-Object System.Collections.Generic.List[object]
$nameCount = @{}
foreach ($c in $wcfClusters) { $k = $c.Name.ToLowerInvariant(); $nameCount[$k] = 1 + [int]$nameCount[$k] }

foreach ($c in $wcfClusters) {
    $hand = @($c.Occs | Where-Object { $_.Role -in 'Host', 'Implementation', 'ContractOnly' })
    $hostRepos = @($hand | Where-Object Role -eq 'Host' | Select-Object -ExpandProperty Repo -Unique)
    $implRepos = @($hand | Where-Object Role -eq 'Implementation' | Select-Object -ExpandProperty Repo -Unique)
    $defRepos  = @($c.Occs | Where-Object { -not $_.IsPseudo -and -not $_.IsGen } | Select-Object -ExpandProperty Repo -Unique)
    $proxyRepos = @($c.Occs | Where-Object { $_.Role -eq 'ProxyClient' } | Select-Object -ExpandProperty Repo -Unique)
    $flags = New-Object System.Collections.Generic.List[string]
    $owner = ''; $ownerConf = ''
    if ($hostRepos.Count -eq 1) { $owner = $hostRepos[0]; $ownerConf = 'High' }
    elseif ($hostRepos.Count -gt 1) { $owner = Select-Owner $hostRepos; $ownerConf = 'Low'; $flags.Add('MultiHost') }
    elseif ($implRepos.Count -ge 1) { $owner = Select-Owner $implRepos; $ownerConf = 'Medium'; $flags.Add('NoHostEvidence') }
    else { $flags.Add('NoHostFound') }
    if ($defRepos.Count -gt 1) { $flags.Add('DefinedInMultipleRepos') }
    if (@($c.Occs | Where-Object IsDrifted).Count -gt 0) { $flags.Add('ContractDrift') }
    if ($nameCount[$c.Name.ToLowerInvariant()] -gt 1) { $flags.Add('NameCollision') }
    if ($c.PSObject.Properties['ExtraFlags']) { foreach ($x in $c.ExtraFlags) { $flags.Add($x) } }

    $allOps = @($c.Occs | ForEach-Object { $_.Ops } | Where-Object { $_ } | Sort-Object -Unique)
    $svcFiles = @($hostRepos + $implRepos | Select-Object -Unique | ForEach-Object {
        $r = $repos[$_]; foreach ($k in $r.SvcFiles.Keys) { if (@($c.Occs | Where-Object { $_.Repo -eq $r.Name -and ($_.ImplClasses -contains $k) }).Count -gt 0 -or $r.HostedContracts.Count -eq 0) { $r.SvcFiles[$k] } }
    } | Select-Object -Unique)
    $defProj = @($c.Occs | Where-Object { -not $_.IsGen -and -not $_.IsPseudo } | ForEach-Object { $_.ProjectNames } | Where-Object { $_ } | Sort-Object -Unique)
    $clusterRows.Add([pscustomobject][ordered]@{
        ClusterId = $c.Id; Kind = 'WCF'; Name = $c.Name; Owner = $owner; OwnerConfidence = $ownerConf
        HostRepos = ($hostRepos -join ';'); ImplRepos = ($implRepos -join ';'); DefinitionRepos = ($defRepos -join ';'); ProxyRepos = ($proxyRepos -join ';')
        OperationCount = $allOps.Count; Operations = ($allOps -join ';')
        InterfaceNames = (($c.Occs | Select-Object -ExpandProperty Iface -Unique) -join ';'); ClientClasses = ($c.ClientClasses -join ';')
        ClrNamespaces = (($c.Occs | Where-Object ClrNs | Select-Object -ExpandProperty ClrNs -Unique) -join ';'); WireNamespace = $c.WireNs
        SvcFiles = ($svcFiles -join ';'); DefinitionProjectNames = ($defProj -join ';'); HttpVerb = ''; Route = ''; RouteNormalized = ''; Flags = ($flags -join ';')
    })

    foreach ($o in $c.Occs) {
        $base = [ordered]@{
            ClusterId = $c.Id; Kind = 'WCF'; Role = $o.Role; Confidence = $o.Confidence; Resolution = $o.Resolution; Repo = $o.Repo; Project = $o.Project
            File = $o.Rel; Line = $o.Line; ContractOrController = $o.Iface; ClrNamespace = $o.ClrNs
        }
        $tail = [ordered]@{
            WireNamespace = $o.WireNs; IsGenerated = $o.IsGen; IsDrifted = $o.IsDrifted; ProjectNames = ($o.ProjectNames -join ';')
            ConfigurationName = $o.ConfigName; ImplClasses = ($o.ImplClasses -join ';'); Framework = 'WCF'
        }
        if (@($o.OpDetails).Count -gt 0) {
            foreach ($d in $o.OpDetails) {
                $row = [ordered]@{}; foreach ($k in $base.Keys) { $row[$k] = $base[$k] }
                $row['Line'] = $d.Line; $row['Operation'] = $d.Name; $row['MethodName'] = $d.Method; $row['HttpVerb'] = $d.Verb; $row['Route'] = $d.Uri; $row['RouteNormalized'] = ''; $row['SoapAction'] = $d.Action
                foreach ($k in $tail.Keys) { $row[$k] = $tail[$k] }
                $row['Flags'] = ($flags -join ';')
                $invRows.Add([pscustomobject]$row)
            }
        } else {
            $row = [ordered]@{}; foreach ($k in $base.Keys) { $row[$k] = $base[$k] }
            $row['Operation'] = '*'; $row['MethodName'] = ''; $row['HttpVerb'] = ''; $row['Route'] = ''; $row['RouteNormalized'] = ''; $row['SoapAction'] = ''
            foreach ($k in $tail.Keys) { $row[$k] = $tail[$k] }
            $row['Flags'] = ($flags -join ';')
            $invRows.Add([pscustomobject]$row)
        }
    }
}

# --- REST: one cluster per (verb, normalised route). Same route in several repos = duplicate definitions to review.
foreach ($g in ($routeRows | Group-Object { $_.Verb + '|' + $_.RouteNorm })) {
    $first = $g.Group[0]
    $id = 'REST-' + (Get-ShortHash $g.Name)
    $defRepos = @($g.Group | Select-Object -ExpandProperty Repo -Unique)
    $hostRepos = @($g.Group | Where-Object IsWeb | Select-Object -ExpandProperty Repo -Unique)
    $flags = New-Object System.Collections.Generic.List[string]
    $owner = ''; $ownerConf = ''
    if ($hostRepos.Count -eq 1) { $owner = $hostRepos[0]; $ownerConf = 'High' }
    elseif ($hostRepos.Count -gt 1) { $owner = Select-Owner $hostRepos; $ownerConf = 'Low'; $flags.Add('DuplicateRoute') }
    elseif ($defRepos.Count -eq 1) { $owner = $defRepos[0]; $ownerConf = 'Medium'; $flags.Add('NoWebProject') }
    else { $owner = Select-Owner $defRepos; $ownerConf = 'Low'; $flags.Add('DuplicateRoute') }
    if ($defRepos.Count -gt 1 -and -not $flags.Contains('DuplicateRoute')) { $flags.Add('DuplicateRoute') }
    if (@($g.Group | Where-Object Flag | Select-Object -ExpandProperty Flag -Unique).Count -gt 0) { foreach ($x in ($g.Group | Where-Object Flag | Select-Object -ExpandProperty Flag -Unique)) { if (-not $flags.Contains($x)) { $flags.Add($x) } } }
    $routes = @($g.Group | Select-Object -ExpandProperty Route -Unique)
    $clusterRows.Add([pscustomobject][ordered]@{
        ClusterId = $id; Kind = 'REST'; Name = ($first.Verb + ' /' + $first.RouteNorm); Owner = $owner; OwnerConfidence = $ownerConf
        HostRepos = ($hostRepos -join ';'); ImplRepos = ''; DefinitionRepos = ($defRepos -join ';'); ProxyRepos = ''
        OperationCount = 1; Operations = (($g.Group | ForEach-Object { $_.Controller + '.' + $_.Method } | Select-Object -Unique) -join ';')
        InterfaceNames = ''; ClientClasses = ''; ClrNamespaces = ''; WireNamespace = ''; SvcFiles = ''; DefinitionProjectNames = ''
        HttpVerb = $first.Verb; Route = ($routes -join ';'); RouteNormalized = $first.RouteNorm; Flags = ($flags -join ';')
    })
    foreach ($r in $g.Group) {
        $invRows.Add([pscustomobject][ordered]@{
            ClusterId = $id; Kind = 'REST'; Role = $(if ($r.IsWeb) { 'Host' } else { 'Library' }); Confidence = 'High'; Resolution = 'Declared'; Repo = $r.Repo; Project = $r.Project
            File = $r.Rel; Line = $r.Line; ContractOrController = $r.Controller; ClrNamespace = $r.ClrNs; Operation = $r.Method; MethodName = $r.Method
            HttpVerb = $r.Verb; Route = $r.Route; RouteNormalized = $r.RouteNorm; SoapAction = ''; WireNamespace = ''; IsGenerated = $false; IsDrifted = $false
            ProjectNames = ''; ConfigurationName = ''; ImplClasses = ''; Framework = $r.Framework; Flags = ($flags -join ';')
        })
    }
}

# --- repo references (used by step 2 to attribute name matches correctly)
$refRows = foreach ($repo in $repos.Values) {
    foreach ($p in $repo.Projects.Values) {
        foreach ($x in $p.Packages) { [pscustomobject]@{ Repo = $repo.Name; RefType = 'Package'; RefName = $x } }
        foreach ($x in $p.ProjRefs) { [pscustomobject]@{ Repo = $repo.Name; RefType = 'Project'; RefName = $x } }
        foreach ($x in $p.AsmRefs)  { [pscustomobject]@{ Repo = $repo.Name; RefType = 'Assembly'; RefName = $x } }
    }
}

$repos.Values | Select-Object Name, Dir, Remote, LastCommit | Export-Csv -Path (Join-Path $OutputPath 'repos.csv') -NoTypeInformation -Encoding UTF8
$clusterRows | Export-Csv -Path (Join-Path $OutputPath 'endpoint-clusters.csv') -NoTypeInformation -Encoding UTF8
$invRows     | Export-Csv -Path (Join-Path $OutputPath 'endpoint-inventory.csv') -NoTypeInformation -Encoding UTF8
@($refRows | Sort-Object Repo, RefType, RefName -Unique) | Export-Csv -Path (Join-Path $OutputPath 'repo-references.csv') -NoTypeInformation -Encoding UTF8
$warnings    | Export-Csv -Path (Join-Path $OutputPath 'scan-warnings.csv') -NoTypeInformation -Encoding UTF8

# --- console summary
$wc = @($clusterRows | Where-Object Kind -eq 'WCF'); $rc = @($clusterRows | Where-Object Kind -eq 'REST')
Write-Host ''
Write-Host "==== Inventory summary ====" -ForegroundColor Cyan
Write-Host ("Repos scanned            : {0}" -f $repos.Count)
Write-Host ("Unique WCF contracts     : {0}  (from {1} declarations - duplicates merged)" -f $wc.Count, $contractOccs.Count)
Write-Host ("Unique REST endpoints    : {0}  (from {1} route declarations)" -f $rc.Count, $routeRows.Count)
foreach ($fl in 'MultiHost', 'DefinedInMultipleRepos', 'NameCollision', 'ContractDrift', 'NoHostFound', 'DuplicateRoute') {
    $n = @($clusterRows | Where-Object { ";$($_.Flags);" -like "*;$fl;*" }).Count
    if ($n) { Write-Host ("  flagged {0,-24}: {1}" -f $fl, $n) -ForegroundColor Yellow }
}
Write-Host ("Warnings to review       : {0}  -> scan-warnings.csv" -f $warnings.Count)
Write-Host "Output folder            : $OutputPath"
