# catches: a blocker-retry loop that deviates from doc 12 §4.2 — ignores a ceiling (spins past
#          maxAttempts/totalWaitSeconds), consumes the logic-retry budget on a transient, or is not
#          floored by transientPauseBudgetSeconds. Runs the authored BlockerRetryTests; re-emits failure
#          detail at the END so the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~BlockerRetryTests" --nologo 2>&1
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
    Write-Output "blocker-retry tests failing — the bounded wait/backoff ceilings or the budget-floor are not to spec (see details above)"
    exit 1
}
exit 0
