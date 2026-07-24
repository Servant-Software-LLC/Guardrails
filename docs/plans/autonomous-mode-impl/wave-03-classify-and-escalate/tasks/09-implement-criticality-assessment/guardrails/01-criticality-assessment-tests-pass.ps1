# catches: a criticality decider that deviates from doc 12 — worst of all, treating a malformed
#          assessment as a proceed-best-guess (the judge becoming the verdict authority, violating
#          invariant 1) or letting a high/critical hard call best-guess under proceed-unreviewed (the
#          clamp not applied, §5.2). Runs the authored CriticalityAssessmentTests; re-emits failure
#          detail at the END so the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~CriticalityAssessmentTests" --nologo 2>&1
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
    Write-Output "criticality-assessment tests failing — the threshold compare / malformed⇒escalate / proceed-unreviewed clamp / maxJudgeWidenings cap is not to spec (see details above)"
    exit 1
}
exit 0
