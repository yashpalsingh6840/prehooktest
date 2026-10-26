# EndpointScan.Common.ps1
# Shared helpers for Get-EndpointInventory.ps1 and Find-EndpointUsage.ps1.
# Dot-sourced by both scripts - keep this file in the same folder as them.
# Compatible with Windows PowerShell 5.1 and PowerShell 7+.

if (-not ('CsMasker' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
public static class CsMasker
{
    static void Blank(char[] a, string s, int from, int to)
    {
        if (to > s.Length) to = s.Length;
        for (int k = from; k < to; k++) { if (s[k] != '\n' && s[k] != '\r') a[k] = ' '; }
    }

    // Returns { clean, mask }, both exactly the same length as the input (so indexes line up):
    //   clean = comments blanked out, string contents kept (so we can read namespaces / routes)
    //   mask  = comments AND string/char contents blanked (so braces/brackets inside strings can't confuse parsing)
    public static string[] Process(string s)
    {
        int n = s.Length;
        char[] c = s.ToCharArray();
        char[] m = s.ToCharArray();
        int i = 0;
        while (i < n)
        {
            char ch = s[i];
            if (ch == '/' && i + 1 < n && s[i + 1] == '/')
            {
                while (i < n && s[i] != '\n' && s[i] != '\r') { c[i] = ' '; m[i] = ' '; i++; }
            }
            else if (ch == '/' && i + 1 < n && s[i + 1] == '*')
            {
                int j = i + 2;
                while (j + 1 < n && !(s[j] == '*' && s[j + 1] == '/')) j++;
                int end = Math.Min(j + 2, n);
                for (int k = i; k < end; k++) { if (s[k] != '\n' && s[k] != '\r') { c[k] = ' '; m[k] = ' '; } }
                i = end;
            }
            else if (ch == '"')
            {
                if (i + 2 < n && s[i + 1] == '"' && s[i + 2] == '"')
                {
                    // raw string literal """ ... """
                    int q = 0; while (i + q < n && s[i + q] == '"') q++;
                    int j = i + q; int close = -1;
                    while (j < n)
                    {
                        if (s[j] == '"')
                        {
                            int r = 0; while (j + r < n && s[j + r] == '"') r++;
                            if (r >= q) { close = j; break; }
                            j += r;
                        }
                        else j++;
                    }
                    if (close < 0) { Blank(m, s, i + q, n); i = n; }
                    else { Blank(m, s, i + q, close); i = close + q; }
                }
                else
                {
                    bool verbatim = (i > 0 && s[i - 1] == '@') || (i > 1 && s[i - 1] == '$' && s[i - 2] == '@');
                    int j = i + 1;
                    if (verbatim)
                    {
                        while (j < n)
                        {
                            if (s[j] == '"') { if (j + 1 < n && s[j + 1] == '"') { j += 2; continue; } break; }
                            j++;
                        }
                    }
                    else
                    {
                        while (j < n && s[j] != '"' && s[j] != '\n')
                        {
                            if (s[j] == '\\' && j + 1 < n) j += 2; else j++;
                        }
                    }
                    Blank(m, s, i + 1, j);
                    i = (j < n && s[j] == '"') ? j + 1 : j;
                    if (i <= j && j < n && s[j] != '"') i = j; // unterminated: resume at newline
                }
            }
            else if (ch == '\'')
            {
                if (i + 2 < n && s[i + 1] == '\\')
                {
                    int j = i + 2;
                    while (j < n && s[j] != '\'' && s[j] != '\n') j++;
                    Blank(m, s, i + 1, j);
                    i = Math.Min(j + 1, n);
                }
                else if (i + 2 < n && s[i + 2] == '\'')
                {
                    m[i + 1] = ' ';
                    i += 3;
                }
                else i++;
            }
            else i++;
        }
        return new string[] { new string(c), new string(m) };
    }

    public static int MatchBrace(string mask, int open)
    {
        int depth = 0;
        for (int i = open; i < mask.Length; i++)
        {
            char ch = mask[i];
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) return i; }
        }
        return mask.Length - 1;
    }

    public static int Line(string s, int idx)
    {
        int l = 1;
        if (idx > s.Length) idx = s.Length;
        for (int i = 0; i < idx; i++) if (s[i] == '\n') l++;
        return l;
    }
}
'@
}

function Get-ShortHash([string]$s) {
    $sha = [System.Security.Cryptography.SHA1]::Create()
    $b = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($s))
    (($b[0..4] | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Get-ShortName([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return '' }
    $s = ($s.Trim() -replace '<.*$', '')
    ($s -split '\.')[-1]
}

function Get-Val($match, [string]$group, [string]$clean) {
    $g = $match.Groups[$group]
    if ($g.Success) { return $clean.Substring($g.Index, $g.Length) }
    return ''
}

function Read-Text([string]$path, [int]$maxMB = 5) {
    try {
        $fi = New-Object System.IO.FileInfo($path)
        if ($fi.Length -gt ($maxMB * 1MB)) { return $null }
        return [System.IO.File]::ReadAllText($path)
    } catch { return $null }
}

$script:TestSegRx = New-Object System.Text.RegularExpressions.Regex('(?i)(^|[._-])(unit|integration|functional|e2e|acceptance|load|perf)?tests?([._-]|$)|(?-i:[a-z0-9]Tests?(\.cs)?$)')
function Test-IsTestPath([string]$rel) {
    foreach ($seg in ($rel -split '[\\/]')) { if ($script:TestSegRx.IsMatch($seg)) { return $true } }
    return $false
}

function Find-Repos {
    param([string]$Root, [string[]]$ExcludeDir, [string[]]$ExcludeRepo)
    $Root = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\', '/')
    $found = New-Object System.Collections.Generic.List[string]
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($Root)
    while ($stack.Count -gt 0) {
        $d = $stack.Pop()
        if (Test-Path -LiteralPath ([System.IO.Path]::Combine($d, '.git'))) { $found.Add($d); continue }
        try {
            foreach ($s in [System.IO.Directory]::EnumerateDirectories($d)) {
                $n = [System.IO.Path]::GetFileName($s)
                if ($ExcludeDir -notcontains $n) { $stack.Push($s) }
            }
        } catch { }
    }
    # First-level folders that are not (and do not contain) a git repo - e.g. exported/zipped sources - are treated as repos too.
    $sep = [System.IO.Path]::DirectorySeparatorChar
    foreach ($s in [System.IO.Directory]::EnumerateDirectories($Root)) {
        if ($ExcludeDir -contains [System.IO.Path]::GetFileName($s)) { continue }
        $covered = $false
        foreach ($f in $found) { if ($f -eq $s -or $f.StartsWith($s + $sep) -or $s.StartsWith($f + $sep)) { $covered = $true; break } }
        if (-not $covered) { $found.Add($s); Write-Host "  note: '$([System.IO.Path]::GetFileName($s))' has no .git folder - treated as a repo" -ForegroundColor Yellow }
    }
    return @($found | Where-Object { $ExcludeRepo -notcontains (Split-Path $_ -Leaf) } | Sort-Object)
}

function Get-RepoMeta {
    param([string]$Dir, [switch]$SkipGit)
    $remote = ''
    $cfg = [System.IO.Path]::Combine($Dir, '.git', 'config')
    if (Test-Path -LiteralPath $cfg -PathType Leaf) {
        try {
            $t = [System.IO.File]::ReadAllText($cfg)
            $m = [regex]::Match($t, '\[remote "origin"\][^\[]*?url\s*=\s*(\S+)')
            if ($m.Success) { $remote = $m.Groups[1].Value }
        } catch { }
    }
    $norm = ''
    if ($remote) {
        $norm = $remote.ToLowerInvariant() -replace '^[a-z+]+://', '' -replace '^[^@/]+@', '' -replace ':', '/' -replace '\.git$', '' -replace '/+$', ''
    }
    $last = [datetime]::MinValue
    if (-not $SkipGit -and (Test-Path -LiteralPath ([System.IO.Path]::Combine($Dir, '.git'))) -and (Get-Command git -ErrorAction SilentlyContinue)) {
        try {
            $iso = & git -C $Dir log -1 --format=%cI 2>$null
            if ($iso) { $last = [datetime]::Parse($iso, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal) }
        } catch { }
    }
    [pscustomobject]@{
        Name = (Split-Path $Dir -Leaf); Dir = $Dir; Remote = $remote; RemoteNorm = $norm; LastCommit = $last
    }
}

function Get-RepoFiles {
    param([string]$Dir, [string[]]$ExcludeDir)
    $res = New-Object System.Collections.Generic.List[string]
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($Dir)
    while ($stack.Count -gt 0) {
        $d = $stack.Pop()
        try {
            foreach ($f in [System.IO.Directory]::EnumerateFiles($d)) { $res.Add($f) }
            foreach ($s in [System.IO.Directory]::EnumerateDirectories($d)) {
                $n = [System.IO.Path]::GetFileName($s)
                if ($ExcludeDir -notcontains $n) { $stack.Push($s) }
            }
        } catch { }
    }
    return $res
}

function Get-Jaccard($a, $b) {
    $sa = New-Object 'System.Collections.Generic.HashSet[string]' (, [string[]]@($a | ForEach-Object { "$_".ToLowerInvariant() }))
    $sb = New-Object 'System.Collections.Generic.HashSet[string]' (, [string[]]@($b | ForEach-Object { "$_".ToLowerInvariant() }))
    if ($sa.Count -eq 0 -and $sb.Count -eq 0) { return 1.0 }
    $i = 0
    foreach ($x in $sa) { if ($sb.Contains($x)) { $i++ } }
    $u = $sa.Count + $sb.Count - $i
    if ($u -eq 0) { return 1.0 }
    return [double]$i / [double]$u
}

function Normalize-Route([string]$r) {
    if ($null -eq $r) { return '' }
    $r = ($r -split '[?#]')[0]
    $r = $r.Trim().Trim('/').ToLowerInvariant()
    $r = [regex]::Replace($r, '\{[^}]*\}', '{}')
    return $r
}

# NOTE: the leading comma stops PowerShell from unrolling an (empty) set into $null when it is returned from a function.
function New-CiSet { return , (New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)) }
