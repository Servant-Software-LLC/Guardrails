# Two-sided sample probe for guardrails/01-folder-gone.ps1 (#468/#302).
#
# This task is the one deliverable in the wave with NO test anywhere - a deletion leaves no artifact to
# assert on, so its guardrail IS the entire proof. That is why the proof gets a durable probe of its own
# rather than a hand-run at authoring time, and why the VALID half runs the REAL action.ps1 rather than
# simulating what it does.
#
#   INVALID a - the folder still present            -> exit 1 + "still exists"
#   VALID     - the real action.ps1 has just run    -> action exit 0, guardrail exit 0
#   INVALID b - the .md sibling over-deleted        -> exit 1 + "path PREFIX"
#   INVALID c - this plan's own folder over-deleted -> exit 1 + "currently executing"
#   INVALID d - a FILE at the folder's path         -> exit 1 + "not a directory"
#
# Read-only against the repo: every half runs in its own %TEMP% tree.
#
#   pwsh -NoProfile -File <plan>/wave-04-report-and-cleanup/tasks/03-delete-superseded-plan-folder/samples/01-folder-gone.probe.ps1

$ErrorActionPreference = 'Stop'
$gate   = Join-Path $PSScriptRoot '..\guardrails\01-folder-gone.ps1'
$action = Join-Path $PSScriptRoot '..\action.ps1'

function New-Tree([bool]$WithFolder) {
    $t = Join-Path ([System.IO.Path]::GetTempPath()) ('gr-s3w4t3-' + [guid]::NewGuid().ToString('N'))
    # The two over-deletion victims are present in EVERY tree: they are what must survive.
    New-Item -ItemType Directory -Path (Join-Path $t 'docs/plans/model-tiering-stage-3') -Force | Out-Null
    Set-Content -Path (Join-Path $t 'docs/plans/pilot-seat-model-provenance.md') -Value 'design doc' -NoNewline
    if ($WithFolder) {
        New-Item -ItemType Directory -Path (Join-Path $t 'docs/plans/pilot-seat-model-provenance/tasks/01-x/guardrails') -Force | Out-Null
        Set-Content -Path (Join-Path $t 'docs/plans/pilot-seat-model-provenance/guardrails.json') -Value '{}' -NoNewline
        Set-Content -Path (Join-Path $t 'docs/plans/pilot-seat-model-provenance/tasks/01-x/task.json') -Value '{}' -NoNewline
    }
    return $t
}

function Invoke-Script($tree, $script) {
    Push-Location $tree
    $o = & pwsh -NoProfile -File $script 2>&1
    $e = $LASTEXITCODE
    Pop-Location
    return @{ Exit = $e; Out = ($o | Out-String) }
}

$fail = 0
$trees = @()

# --- INVALID a: the folder is still there -----------------------------------------------------------
$t = New-Tree $true; $trees += $t
$r = Invoke-Script $t $gate
if ($r.Exit -ne 0 -and $r.Out -match 'still exists') { Write-Output 'INVALID (folder present)            -> exit 1, own message   OK' }
else { Write-Output "INVALID (folder present)            -> exit $($r.Exit)   *** NO TEETH ***"; $fail++ }

# --- VALID: run the REAL action, then the gate ------------------------------------------------------
$ra = Invoke-Script $t $action
$r  = Invoke-Script $t $gate
if ($ra.Exit -eq 0 -and $r.Exit -eq 0) { Write-Output "VALID (after the real action.ps1)   -> action exit 0, guardrail exit 0   OK  [$($ra.Out.Trim())]" }
else { Write-Output "VALID (after the real action.ps1)   -> action $($ra.Exit) / guardrail $($r.Exit)   *** FALSE RED ***"; Write-Output $r.Out; $fail++ }

# --- INVALID b: the .md sibling taken by a trailing wildcard ----------------------------------------
$t2 = New-Tree $false; $trees += $t2
Remove-Item (Join-Path $t2 'docs/plans/pilot-seat-model-provenance.md') -Force
$r = Invoke-Script $t2 $gate
if ($r.Exit -ne 0 -and $r.Out -match 'path PREFIX') { Write-Output 'INVALID (.md over-deleted)          -> exit 1, own message   OK' }
else { Write-Output "INVALID (.md over-deleted)          -> exit $($r.Exit)   *** NO TEETH ***"; $fail++ }

# --- INVALID c: a broader docs/plans/ sweep ---------------------------------------------------------
$t3 = New-Tree $false; $trees += $t3
Remove-Item (Join-Path $t3 'docs/plans/model-tiering-stage-3') -Recurse -Force
$r = Invoke-Script $t3 $gate
if ($r.Exit -ne 0 -and $r.Out -match 'currently executing') { Write-Output 'INVALID (plan folder over-deleted)  -> exit 1, own message   OK' }
else { Write-Output "INVALID (plan folder over-deleted)  -> exit $($r.Exit)   *** NO TEETH ***"; $fail++ }

# --- INVALID d: a file where the directory was ------------------------------------------------------
$t4 = New-Tree $false; $trees += $t4
Set-Content -Path (Join-Path $t4 'docs/plans/pilot-seat-model-provenance') -Value 'x' -NoNewline
$r = Invoke-Script $t4 $gate
if ($r.Exit -ne 0 -and $r.Out -match 'not a directory') { Write-Output 'INVALID (file at the folder path)   -> exit 1, own message   OK' }
else { Write-Output "INVALID (file at the folder path)   -> exit $($r.Exit)   *** NO TEETH ***"; $fail++ }

foreach ($x in $trees) { Remove-Item $x -Recurse -Force -ErrorAction SilentlyContinue }

Write-Output ''
if ($fail -eq 0) { Write-Output 'RESULT: all 5 halves behave - and the VALID one ran the real action.'; exit 0 }
Write-Output "RESULT: $fail half/halves DEFECTIVE."; exit 1
