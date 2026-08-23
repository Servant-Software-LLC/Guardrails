# Two-sided sample probe for preflights/01-wave2-surfaces-materialized.ps1 (#468/#302). See README.md.
#
#   VALID   half - a tree carrying the real anchored files, unmodified  -> expect exit 0.
#   INVALID half - PER CLAUSE: the same tree with that ONE clause's matches deleted from its own file
#                  -> expect exit 1 AND that clause's own message present. This is the half that catches
#                  a clause which can NEVER fire (wave 2 shipped exactly that bug in its first draft: a
#                  path-keyed hashtable of clause LISTS, which PowerShell unwrapped for every
#                  single-clause file, silently disabling four clauses).
#
# The clause list is lifted from the guardrail itself, so adding an anchor tests it here automatically.
# Read-only against the repo: everything happens in a %TEMP% copy.

param(
    # The tree to SEED the sample halves from. Defaults to the current directory when that looks like a
    # workspace root, else to the plan folder's own repo.
    #
    # This parameter is not ceremony - it is a defect this probe found in its own first draft. Every
    # anchor in the wave-3 entry gate is a WAVE 2 deliverable, which exists on the plan BRANCH (the
    # materialized integration worktree) and NOT in the maintainer's checkout, where waves are authored.
    # Seeding from the plan folder's repo therefore reported three anchors "missing" and both halves
    # DEFECTIVE, when the gate and the anchors were both correct. Run it from the tree the wave will
    # actually execute against:
    #
    #   cd <worktreeRoot>/<runId>/_integration
    #   pwsh -NoProfile -File <plan>/wave-03-operator-surfaces/samples/01-wave2-surfaces-materialized.probe.ps1
    [string]$Repo
)

$ErrorActionPreference = 'Stop'
if (-not $Repo) {
    $Repo = if (Test-Path (Join-Path (Get-Location).Path 'Guardrails.sln')) { (Get-Location).Path }
            else { (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..')).Path }
}
$repo = (Resolve-Path $Repo).Path
$gate = Join-Path $PSScriptRoot '..\preflights\01-wave2-surfaces-materialized.ps1'
Write-Output "seed tree: $repo"

$src = Get-Content -Raw -Path $gate
$m = [regex]::Match($src, '(?ms)^\$anchors = @\((.*?)^\)\s*$')
if (-not $m.Success) { Write-Output 'ABORT: could not lift $anchors from the guardrail'; exit 9 }
$anchors = Invoke-Expression ('@(' + $m.Groups[1].Value + ')')
$files = $anchors | ForEach-Object { $_[0] } | Sort-Object -Unique

function New-Tree {
    $t = Join-Path ([System.IO.Path]::GetTempPath()) ('gr-s3w3p-' + [guid]::NewGuid().ToString('N'))
    foreach ($f in $files) {
        $dst = Join-Path $t $f
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        $seed = Join-Path $repo $f
        if (Test-Path $seed) { Copy-Item $seed $dst }
        else { Write-Output "ABORT: seed missing in the repo: $f"; exit 9 }
    }
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
if ($v.Exit -eq 0) { Write-Output "VALID (real anchored tree) -> exit 0, 0 clauses firing   OK" }
else {
    Write-Output "VALID (real anchored tree) -> exit $($v.Exit), $($v.Lines.Count) firing   *** FALSE RED ***"
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

if ($dead.Count -eq 0) {
    Write-Output "INVALID (per-clause removal) -> all $($anchors.Count) clauses fired individually   OK"
}
else {
    Write-Output "INVALID (per-clause removal) -> $($dead.Count) DEAD clause(s):"
    $dead | ForEach-Object { Write-Output "    $_" }
    $fail++
}

Remove-Item $tree -Recurse -Force -ErrorAction SilentlyContinue

Write-Output ''
if ($fail -eq 0) { Write-Output "RESULT: both halves behave. $($anchors.Count) anchors over $($files.Count) files, each individually live."; exit 0 }

# The wrong-tree case is the LIKELIEST reason to be reading this line, so say so here rather than only in
# the -Repo comment forty lines up. Measured at review: run from a maintainer checkout the probe reports
# 3 dead clauses and DEFECTIVE, and the gate is entirely correct — every one of those anchors is a WAVE 2
# deliverable that exists only on the plan branch. A verdict that cries wolf teaches the next reader to
# skim it, which costs more than the probe is worth.
$absent = @($anchors | Where-Object {
    $seedFile = Join-Path $repo $_[0]
    (Test-Path $seedFile) -and ((Get-Content -Raw -Path $seedFile) -notmatch $_[1])
})
if ($absent.Count -gt 0) {
    Write-Output "NOTE: $($absent.Count) anchor(s) are absent from the SEED TREE itself, so this is very likely the"
    Write-Output "      wrong tree rather than a defective gate. This is a wave-3 ENTRY gate: every anchor is a"
    Write-Output "      WAVE 2 deliverable and exists only on the merged wave-2 HEAD. Re-run against that tree:"
    Write-Output "        -Repo <worktreeRoot>/<runId>/_integration      (or any checkout of the plan branch)"
    Write-Output "      Seed tree used: $repo"
}
Write-Output "RESULT: $fail half/halves DEFECTIVE."; exit 1
