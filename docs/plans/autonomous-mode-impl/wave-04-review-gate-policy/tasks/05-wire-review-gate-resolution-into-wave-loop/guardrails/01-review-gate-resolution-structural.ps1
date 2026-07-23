# catches: a wave loop that BUILDS against the review-gate config surface but never RESOLVES it — the
#          Scheduler still never consults gateThresholds.review-gate or records a proceeded-unreviewed
#          decision, so Option P is dead and an unreviewed wave can only ever halt. Cheap STRUCTURAL
#          fast-fail complement to the drive-the-real-Scheduler test (guardrail 03): requires BOTH new
#          references in Scheduler.cs — 'ReviewGate' (reads the threshold) AND 'ProceededUnreviewed'
#          (records the Option-P decision). Both are NEW to this task (the shipped escalationSink was wired
#          by wave 3, so requiring it would not prove THIS wire). Scoped to the one file this task owns.
$scheduler = "src/Guardrails.Core/Execution/Scheduler.cs"
if (-not (Test-Path $scheduler)) {
    Write-Output "$scheduler does not exist"
    exit 1
}
$sc = Get-Content -Raw -Path $scheduler
if ($sc -notmatch 'ReviewGate') {
    Write-Output "Scheduler.cs never references ReviewGate — the review-gate resolution does not read plan.Config.Autonomy.GateThresholds.ReviewGate; Option E vs Option P is not wired into RunWavedAsync."
    exit 1
}
if ($sc -notmatch 'ProceededUnreviewed') {
    Write-Output "Scheduler.cs never records DecisionTokens.ProceededUnreviewed — the Option-P proceed-unreviewed path does not record its indelible decision (so a proceed-unreviewed run would be indistinguishable from a reviewed one)."
    exit 1
}
exit 0
