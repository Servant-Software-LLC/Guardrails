# catches: an autonomy.jsonl writer that deviates from spec (truncates instead of appends, wrong record
#          shape, or — worst — the additive DecisionEntry change broke existing journal round-tripping).
#          Runs the authored AutonomyForensicRecordsTests AND the pre-existing journal tests (back-compat
#          must survive). Re-emits failure detail at the END so the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AutonomyForensicRecordsTests|FullyQualifiedName~RunJournalTests|FullyQualifiedName~JournalTests" --nologo 2>&1
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
    Write-Output "forensic-records tests failing — the autonomy.jsonl writer or the DecisionEntry additive change is not to spec (see details above)"
    exit 1
}
exit 0
