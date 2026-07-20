# catches: a gate-classifier mapping that deviates from doc 12 §4.1 — worst of all, an UNKNOWN signal
#          classified as retryable (spin on an ambiguous failure) instead of hard-blocker-permanent
#          (escalate), or a terminal-exhaustion needsHuman classified as a judgment-call (best-guessed
#          past a doomed task, violating invariant 5). Runs the authored GateClassifierTests; re-emits
#          failure detail at the END so the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~GateClassifierTests" --nologo 2>&1
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
    Write-Output "gate-classifier tests failing — the signal-to-class mapping (esp. unknown⇒escalate / terminal-exhaustion⇒floor) is not to spec (see details above)"
    exit 1
}
exit 0
