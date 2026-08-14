# catches: a run where every task passed its own checks but the plan's deliverable set is
#          incomplete — a file a later task deleted, or an out/ folder left holding only some
#          of what the plan promised. No single task can prove a whole-run claim.
$ErrorActionPreference = 'Stop'
foreach ($required in @('out/recipient.txt', 'out/greeting.txt', 'out/review.md')) {
    if (-not (Test-Path $required)) {
        Write-Output "$required is missing at the end of the run"
        exit 1
    }
}
exit 0
