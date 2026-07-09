# catches: the final wave leaving the greeting incomplete — a missing greeting.txt/report.md, an
#          empty greeting, or a report that never quotes the real greeting. Wave-02 is the LAST wave,
#          so its exit gate runs on the fully-merged HEAD and IS the whole-plan terminal soundness
#          boundary (SSOT §14.3) — a plan-root <plan>/guardrails/ folder would be optional-additive.
#          Wave-02 is a single linear chain (one leaf), so this terminal postcondition is LOCAL, not
#          scope:"integration" (a full-postcondition check would false-RED at an intermediate union).
if (-not (Test-Path "out/greeting.txt")) {
    Write-Output "out/greeting.txt does not exist — the greeting was never generated"
    exit 1
}
$greeting = (Get-Content -Raw -Path "out/greeting.txt").Trim()
if ([string]::IsNullOrWhiteSpace($greeting)) {
    Write-Output "out/greeting.txt is empty — no greeting was produced"
    exit 1
}
if (-not (Test-Path "out/report.md")) {
    Write-Output "out/report.md does not exist — the greeting was never reported on"
    exit 1
}
$report = Get-Content -Raw -Path "out/report.md"
if ($report -notmatch [regex]::Escape($greeting)) {
    Write-Output "out/report.md does not quote the generated greeting ('$greeting')"
    exit 1
}
exit 0
