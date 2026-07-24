# catches: a wave-04 deliverable that regressed once all branches merged — the run-outcome policy
#          (mergeOnSuccess-OFF-on-machine-decision), the review-gate resolution (escalate vs
#          proceed-unreviewed), the overwatcher auto-tier gate (+ the byte-identical back-compat), or the
#          distinct proceeded-unreviewed exit code — failing on the merged HEAD. Terminal postcondition
#          for the wave (LOCAL — runs ONCE on the merged wave-04 HEAD). Scoped to wave-04's own test
#          classes, NOT the whole suite (a whole-suite run re-imports the #253 integration fixture leak;
#          GR2028 is satisfied by 01-wave4-union-clean, and 02 covers cross-project compile). Runs ONLY
#          the targeted --filter (never the full unfiltered integration project — #253). Re-emits failure
#          detail at the END so the WHY reaches the harness feedback tail (#179).
$failed = $false
$allOut = @()
$targets = @(
    @{ Proj = 'tests/Guardrails.Core.Tests';        Filter = 'FullyQualifiedName~RunOutcomePolicyTests|FullyQualifiedName~SchedulerReviewGateTests|FullyQualifiedName~OverwatchAutoTierTests' },
    @{ Proj = 'tests/Guardrails.Integration.Tests'; Filter = 'FullyQualifiedName~RunOutcomeWiringTests' }
)
foreach ($t in $targets) {
    $out = dotnet test $t.Proj --filter $t.Filter --nologo 2>&1
    $out | ForEach-Object { Write-Output $_ }
    $allOut += $out
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}
if ($failed) {
    $detail = $allOut |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "wave-04 review-gate-policy / run-outcome / overwatcher-auto-tier tests failing on the merged HEAD — see details above"
    exit 1
}
exit 0
