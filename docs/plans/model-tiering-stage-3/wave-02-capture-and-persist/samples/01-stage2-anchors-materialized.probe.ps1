# Two-sided sample probe for preflights/01-stage2-anchors-materialized.ps1 (#468/#302). See README.md.
#
#   VALID   half - the real tree, unmutated                    -> expect exit 0 (a wave-ENTRY gate is
#                                                                 legitimately green on arrival, #478).
#   INVALID half - EACH of the 13 anchors scrubbed in turn      -> expect exit 1 every time.
#
# The invalid half is the one that matters and the one that already paid: during authoring it caught a
# path-keyed hashtable whose single-element clause lists were UNWRAPPED by PowerShell, leaving four
# clauses that could never fail. The valid half passed both before and after that fix.
#
# The anchor list is lifted from the preflight itself, so adding an anchor tests it here automatically.
# Read-only against the repo: everything happens in a %TEMP% copy.

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..')).Path
$pf   = Join-Path $PSScriptRoot '..\preflights\01-stage2-anchors-materialized.ps1'

$src = Get-Content -Raw -Path $pf
$m = [regex]::Match($src, '(?ms)^\$anchors = @\((.*?)^\)\s*$')
if (-not $m.Success) { Write-Output 'ABORT: could not lift $anchors from the preflight'; exit 9 }
$anchors = Invoke-Expression ('@(' + $m.Groups[1].Value + ')')
$files = $anchors | ForEach-Object { $_[0] } | Sort-Object -Unique

function New-Tree {
    $t = Join-Path ([System.IO.Path]::GetTempPath()) ('gr-s3w2pf-' + [guid]::NewGuid().ToString('N'))
    foreach ($f in $files) {
        $dst = Join-Path $t $f
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        Copy-Item (Join-Path $repo $f) $dst
    }
    return $t
}

function Invoke-Preflight($tree) {
    Push-Location $tree
    $null = & pwsh -NoProfile -File $pf 2>&1
    $e = $LASTEXITCODE
    Pop-Location
    return $e
}

$bad = @()

$t = New-Tree
$validExit = Invoke-Preflight $t
Remove-Item $t -Recurse -Force
if ($validExit -eq 0) { Write-Output "VALID (real tree) -> exit 0   OK" }
else { Write-Output "VALID (real tree) -> exit $validExit   *** FALSE RED - an anchor has MOVED ***"; $bad += 'valid half' }

for ($i = 0; $i -lt $anchors.Count; $i++) {
    $c = $anchors[$i]
    $t = New-Tree
    $target = Join-Path $t $c[0]
    $before = Get-Content -Raw -Path $target
    $after  = [regex]::Replace($before, $c[1], 'ZZ_SCRUBBED_ZZ')
    if ($after -eq $before) {
        Write-Output ("clause {0,2} {1,-46} -> pattern does not match the REAL file *** DEAD CLAUSE ***" -f $i, $c[1])
        $bad += "clause $i is dead"
        Remove-Item $t -Recurse -Force
        continue
    }
    Set-Content -Path $target -Value $after -NoNewline
    $e = Invoke-Preflight $t
    Remove-Item $t -Recurse -Force
    Write-Output ("clause {0,2} {1,-46} -> exit {2} {3}" -f $i, $c[1], $e, $(if ($e -ne 0) { 'ok' } else { '*** CANNOT FAIL ***' }))
    if ($e -eq 0) { $bad += "clause $i ($($c[1]) in $($c[0])) CANNOT FAIL" }
}

Write-Output ''
if ($bad.Count -eq 0) { Write-Output "RESULT: valid half green; all $($anchors.Count) clauses can fail."; exit 0 }
Write-Output "RESULT: $($bad.Count) DEFECT(S):"; $bad | ForEach-Object { Write-Output "  - $_" }; exit 1
