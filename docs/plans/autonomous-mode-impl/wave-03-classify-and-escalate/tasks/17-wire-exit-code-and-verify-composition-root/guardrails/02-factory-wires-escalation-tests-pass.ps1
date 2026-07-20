# catches: the escalation machinery built but NOT fully wired into the production run path — the #120
#          recurring false-green (unit-green, terminal-suite-green, yet dead from the CLI). THIS is the
#          composition-root guardrail and the DAG sink's proof: it runs the authored
#          SchedulerEscalationWiringTests (task 14) IN FULL — all five facts — which drive the REAL
#          SchedulerFactory.Create (never injecting the seam) and assert observable escalation /
#          proceeded-best-guess-injection / blocker-retry / resume-answer-injection / distinct-exit-code
#          behaviour. It lives on THIS final task because ALL components must be wired (factory=15,
#          scheduler+reply-channel=16, exit-code=17) for the full class to pass — 15/16 carry
#          structural/targeted guardrails only. LOCAL (no scope key) — "the whole feature IS wired" can
#          only be true once this task's attempt has run, so it must NOT run at an intermediate union
#          (#120/#250). Runs ONLY the targeted --filter (not the full integration project — #253 fixture
#          leak). Re-emits failure detail at the END so the WHY reaches the harness feedback tail (#179).
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
    Write-Output "scheduler-escalation-wiring composition-root proof failing — the classify-then-act machinery + distinct exit code is not fully wired through the real SchedulerFactory.Create / RunCommand production path (see details above). If facts 1-4 fail, the dispatch/reply-channel bug is in Scheduler.cs (tasks 15/16 own it), not this task's ExitCodes/RunCommand change; escalate rather than editing out of scope."
    exit 1
}
exit 0
