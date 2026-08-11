# catches: (a) the skill still claiming state-mutating git "stays outside allowedTools" - a withholding
#          claim the merge semantics cannot deliver; and (b) the cheaper wrong fix of DELETING the
#          passage outright, which an absence-only check would have passed.
#
# WHITESPACE-NORMALIZED before matching. The stale phrases WRAP ACROSS NEWLINES in the reference files
# ("those stay outside" + NEWLINE + "`allowedTools`"), so a single-line regex silently false-PASSES -
# the same defect class as issue #428, caught here by a live run. Collapse all whitespace runs to one
# space first, so a match does not depend on where the source happens to wrap.
$root = '.claude/skills/guardrails-domain-knowledge/'
if (-not (Test-Path $root)) { Write-Output "$root not found"; exit 1 }
$stale = @()
$sawCorrected = $false
foreach ($file in Get-ChildItem -Path $root -Recurse -File -Include *.md) {
    $raw  = Get-Content -Raw -Path $file.FullName
    $flat = [regex]::Replace($raw, '\s+', ' ')
    if ($flat -match 'stay(s)? outside `?allowedTools' -or $flat -match 'stay ungranted' -or $flat -match 'stays? ungranted') {
        $stale += $file.Name
    }
    if ($flat -match 'allowedTools' -and ($flat -match '(?i)floor' -or $flat -match '(?i)cannot withhold' -or $flat -match '(?i)only grant')) {
        $sawCorrected = $true
    }
}
if ($stale.Count -gt 0) {
    Write-Output ("withholding wording still present in: " + ($stale -join ', ') + " - allowedTools is a FLOOR (it grants, it cannot withhold); reword so it no longer promises a verb is unavailable")
    exit 1
}
if (-not $sawCorrected) {
    Write-Output "no corrected floor-not-ceiling statement found under $root - the stale wording must be REPLACED with the accurate claim (allowedTools is a floor: it grants and cannot withhold), not simply deleted"
    exit 1
}
exit 0
