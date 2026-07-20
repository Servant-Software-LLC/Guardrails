# catches: a wave-03 deliverable that regressed once all branches merged — the gate classifier, the
#          blocker-retry bounds, the criticality assessment/clamp, the escalation sink, the answer-file
#          consumption security matrix, the forensic records, or the composition-root wiring failing on
#          the merged HEAD. Terminal postcondition for the wave (LOCAL — runs ONCE on the merged wave-03
#          HEAD). Scoped to wave-03's own test classes, NOT the whole suite (wave-03 is an INTERMEDIATE
#          wave — a whole-suite run would false-RED on wave-4 tests that do not exist yet, #165). Runs
#          ONLY the targeted --filter (not the full unfiltered integration project — #253 fixture leak).
#          Re-emits failure detail at the END so the WHY reaches the harness feedback tail (#179).
$failed = $false
$allOut = @()
$targets = @(
    @{ Proj = 'tests/Guardrails.Core.Tests';        Filter = 'FullyQualifiedName~AutonomyForensicRecordsTests|FullyQualifiedName~GateClassifierTests|FullyQualifiedName~BlockerRetryTests|FullyQualifiedName~CriticalityAssessmentTests|FullyQualifiedName~EscalationSinkTests|FullyQualifiedName~AnswerFileConsumptionTests' },
    @{ Proj = 'tests/Guardrails.Integration.Tests'; Filter = 'FullyQualifiedName~SchedulerEscalationWiringTests' }
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
    Write-Output "wave-03 classify-then-act / escalation / reply-channel tests failing on the merged HEAD — see details above"
    exit 1
}
exit 0
