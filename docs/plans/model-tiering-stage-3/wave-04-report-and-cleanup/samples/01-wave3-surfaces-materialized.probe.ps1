# Two-sided sample probe for preflights/01-wave3-surfaces-materialized.ps1 (#468/#302). See README.md.
#
#   VALID   half - a tree carrying the real anchored files AND the doomed folder  -> expect exit 0.
#   INVALID half - PER CLAUSE: the same tree with that ONE clause's matches deleted from its own file
#                  -> expect exit 1 AND that clause's own message present. Plus the non-regex clause:
#                  the same tree with the doomed FOLDER removed -> expect exit 1 and its own message.
#
# The INVALID half is the one that pays: it is the only half that can catch a clause which can NEVER fire.
# Under an all-present tree a dead clause and a live one are indistinguishable. Wave 2's entry gate shipped
# exactly that bug (a path-keyed hashtable of clause LISTS, which PowerShell unwrapped for every
# single-clause file, silently disabling four clauses) and its invalid half is what found it.
#
# The clause list is lifted from the guardrail itself, so adding an anchor tests it here automatically.
# Read-only against the repo: everything happens in a %TEMP% copy.

param(
    # The tree to SEED the sample halves from. Defaults to the current directory when that looks like a
    # workspace root, else to the plan folder's own repo.
    #
    # Every anchor in this gate is a WAVE 1-3 deliverable, which exists on the plan BRANCH (the materialized
    # integration worktree) and NOT necessarily in the maintainer's checkout, where waves are authored. Run
    # it from the tree the wave will actually execute against:
    #
    #   cd <worktreeRoot>/<runId>/_integration
    #   pwsh -NoProfile -File <plan>/wave-04-report-and-cleanup/samples/01-wave3-surfaces-materialized.probe.ps1
    [string]$Repo
)

$ErrorActionPreference = 'Stop'
if (-not $Repo) {
    $Repo = if (Test-Path (Join-Path (Get-Location).Path 'Guardrails.sln')) { (Get-Location).Path }
            else { (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..')).Path }
}
$repo = (Resolve-Path $Repo).Path
$gate = Join-Path $PSScriptRoot '..\preflights\01-wave3-surfaces-materialized.ps1'
Write-Output "seed tree: $repo"

$src = Get-Content -Raw -Path $gate
$m = [regex]::Match($src, '(?ms)^\$anchors = @\((.*?)^\)\s*$')
if (-not $m.Success) { Write-Output 'ABORT: could not lift $anchors from the guardrail'; exit 9 }
$anchors = Invoke-Expression ('@(' + $m.Groups[1].Value + ')')
$files = $anchors | ForEach-Object { $_[0] } | Sort-Object -Unique

# The one non-regex clause, lifted the same way so it cannot drift from the script.
$dm = [regex]::Match($src, "(?m)^\`$doomed = '([^']+)'")
if (-not $dm.Success) { Write-Output 'ABORT: could not lift $doomed from the guardrail'; exit 9 }
$doomed = $dm.Groups[1].Value

function New-Tree {
    $t = Join-Path ([System.IO.Path]::GetTempPath()) ('gr-s3w4p-' + [guid]::NewGuid().ToString('N'))
    foreach ($f in $files) {
        $dst = Join-Path $t $f
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        $seed = Join-Path $repo $f
        if (Test-Path $seed) { Copy-Item $seed $dst }
        else { Write-Output "ABORT: seed missing in the repo: $f"; exit 9 }
    }
    # The doomed folder is asserted PRESENT by the gate, so the VALID tree must carry it. Its CONTENT is
    # irrelevant to the clause (Test-Path -PathType Container), so a marker file is enough and the probe
    # never copies 57 stale files it does not read.
    New-Item -ItemType Directory -Path (Join-Path $t $doomed) -Force | Out-Null
    Set-Content -Path (Join-Path $t (Join-Path $doomed '.probe-marker')) -Value 'probe' -NoNewline
    return $t
}

function Invoke-Gate($tree) {
    Push-Location $tree
    $o = & pwsh -NoProfile -File $gate 2>&1
    $e = $LASTEXITCODE
    Pop-Location
    return @{ Exit = $e; Lines = @($o | Where-Object { $_ -match '^\s+- ' }) }
}

$fail = 0
$tree = New-Tree

# --- VALID -----------------------------------------------------------------------------------------
$v = Invoke-Gate $tree
if ($v.Exit -eq 0) { Write-Output "VALID (real anchored tree + doomed folder) -> exit 0, 0 clauses firing   OK" }
else {
    Write-Output "VALID (real anchored tree + doomed folder) -> exit $($v.Exit), $($v.Lines.Count) firing   *** FALSE RED ***"
    $v.Lines | ForEach-Object { Write-Output "    $_" }
    $fail++
}

# --- INVALID, one clause at a time -----------------------------------------------------------------
# The mutation is generic: delete every match of THIS clause's own pattern from THIS clause's own file.
# Sibling clauses on the same file may fall over too - that is fine and not asserted. What IS asserted
# is that the clause under test produced its own message.
$originals = @{}
foreach ($f in $files) { $originals[$f] = Get-Content -Raw -Path (Join-Path $tree $f) }

$dead = @()
foreach ($clause in $anchors) {
    $path = $clause[0]
    $pattern = $clause[1]
    $target = Join-Path $tree $path
    $mutated = [regex]::Replace($originals[$path], $pattern, '')
    if ($mutated -eq $originals[$path]) {
        # The pattern did not match the real file at all. That is a DIFFERENT defect (a clause pinned to
        # text that is not there), and the VALID half above would already have caught it - but report it
        # distinctly rather than as a dead clause.
        $dead += "$path :: /$pattern/ does not match the real file - the VALID half should already be red"
        continue
    }
    Set-Content -Path $target -Value $mutated -NoNewline
    $r = Invoke-Gate $tree
    Set-Content -Path $target -Value $originals[$path] -NoNewline

    $fired = @($r.Lines | Where-Object { $_ -match [regex]::Escape($pattern) })
    if ($r.Exit -eq 0 -or $fired.Count -lt 1) {
        $dead += "$path :: /$pattern/ did NOT fire when its own matches were removed (exit $($r.Exit)) - this clause can never fail"
    }
}

# --- INVALID, the non-regex clause -----------------------------------------------------------------
Remove-Item (Join-Path $tree $doomed) -Recurse -Force
$d = Invoke-Gate $tree
$firedDoomed = @($d.Lines | Where-Object { $_ -match [regex]::Escape('has nothing to delete') })
if ($d.Exit -eq 0 -or $firedDoomed.Count -lt 1) {
    $dead += "$doomed :: the deletion-target-present clause did NOT fire when the folder was removed (exit $($d.Exit)) - a wave that starts with the folder already gone would pass this gate, and 03-delete-superseded-plan-folder would go green over a no-op"
}
New-Item -ItemType Directory -Path (Join-Path $tree $doomed) -Force | Out-Null

$total = $anchors.Count + 1
if ($dead.Count -eq 0) {
    Write-Output "INVALID (per-clause removal) -> all $total clauses fired individually   OK"
}
else {
    Write-Output "INVALID (per-clause removal) -> $($dead.Count) DEAD clause(s):"
    $dead | ForEach-Object { Write-Output "    $_" }
    $fail++
}

Remove-Item $tree -Recurse -Force -ErrorAction SilentlyContinue

Write-Output ''
if ($fail -eq 0) { Write-Output "RESULT: both halves behave. $total clauses over $($files.Count) files plus one directory, each individually live."; exit 0 }

# The wrong-tree case is the LIKELIEST reason to be reading this line, so say so here rather than only in
# the -Repo comment sixty lines up. A verdict that cries wolf teaches the next reader to skim it, which
# costs more than the probe is worth.
$absent = @($anchors | Where-Object {
    $seedFile = Join-Path $repo $_[0]
    (Test-Path $seedFile) -and ((Get-Content -Raw -Path $seedFile) -notmatch $_[1])
})
if ($absent.Count -gt 0) {
    Write-Output "NOTE: $($absent.Count) anchor(s) are absent from the SEED TREE itself, so this is very likely the"
    Write-Output "      wrong tree rather than a defective gate. This is a wave-4 ENTRY gate: every anchor is a"
    Write-Output "      wave 1-3 deliverable and several exist only on the merged wave-3 HEAD. Re-run against it:"
    Write-Output "        -Repo <worktreeRoot>/<runId>/_integration      (or any checkout of the plan branch)"
    Write-Output "      Seed tree used: $repo"
}
Write-Output "RESULT: $fail half/halves DEFECTIVE."; exit 1
