# catches: a review-gate resolution that regressed the authored behavior — Option P not running the wave
#          or not recording proceeded-unreviewed, Option E not halting/escalating, or a forged marker. Runs
#          the authored SchedulerReviewGateTests (task 04) driving the REAL Scheduler wave loop; re-emits
#          failure detail at the END so the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~SchedulerReviewGateTests" --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "review-gate-resolution tests failing — the escalate-vs-proceed-unreviewed resolution in RunWavedAsync is not to spec (see details above)"
    exit 1
}
exit 0
