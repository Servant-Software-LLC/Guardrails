# catches: an answer-consumption implementation that passes some cases but fails a SECURITY case — a
#          stale/wrong-identity answer accepted, a double-inject through a broken CAS, a review-gate or
#          clamped-hard-call answer accepted, or the injected text not delimited as untrusted. Runs the
#          authored AnswerFileConsumptionTests (the full DA-7 matrix). Re-emits failure detail at the END
#          so the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AnswerFileConsumptionTests" --nologo 2>&1
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
    Write-Output "answer-consumption security tests failing — a binding / CAS / answerable-gate / injection-delimiting case is not to spec (see details above)"
    exit 1
}
exit 0
