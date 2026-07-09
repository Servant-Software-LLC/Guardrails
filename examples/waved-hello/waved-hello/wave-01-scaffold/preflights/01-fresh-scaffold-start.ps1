# catches: wave-01 running against a stale prior run — the greeting output already exists on the
#          starting branch, so the scaffold wave is not really starting from a clean slate. This is a
#          NEGATIVE baseline (assert the not-yet-produced artifact is ABSENT), correct at plan level /
#          wave-entry only: the wave-entry preflight is skip-once, evaluated when the wave truly starts.
if (Test-Path "out/greeting.txt") {
    Write-Output "out/greeting.txt already exists — this looks like a stale prior run, not a fresh scaffold start; reset the workspace before running wave-01"
    exit 1
}
exit 0
