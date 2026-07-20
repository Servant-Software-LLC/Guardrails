# catches: the escalation machinery built but NOT wired into the production run path — the #120
#          recurring false-green (unit-green, terminal-suite-green, yet dead from the CLI). This is the
#          composition-root guardrail: it runs the authored SchedulerEscalationWiringTests, which drive
#          the REAL SchedulerFactory.Create (never injecting the seam) and assert observable escalation /
#          best-guess / answer-injection output. LOCAL (no scope key) — "the collaborator IS wired" can
#          only be true once THIS task's attempt has run, so it must not run at an intermediate union
#          (#120/#250). Re-emits failure detail at the END so the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~SchedulerEscalationWiringTests" --nologo 2>&1
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
    Write-Output "scheduler-escalation-wiring tests failing — the classify-then-act machinery is not wired into the real SchedulerFactory.Create production path (see details above)"
    exit 1
}
exit 0
