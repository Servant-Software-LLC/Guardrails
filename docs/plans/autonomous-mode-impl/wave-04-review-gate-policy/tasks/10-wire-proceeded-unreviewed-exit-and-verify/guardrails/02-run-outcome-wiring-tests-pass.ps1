# catches: the run-outcome policy built but NOT fully wired into the production run path — the #120
#          recurring false-green (unit-green, terminal-suite-green, yet the real CLI still auto-delivers a
#          best-guessed run / exits green / forges a marker). THIS is the composition-root guardrail and the
#          DAG sink's proof: it runs the authored RunOutcomeWiringTests (task 08) IN FULL — all four facts —
#          which drive the REAL CLI run (never a hand-injected policy) and assert observable delivery-
#          suppression / distinct-exit-5 / N-unreviewed-flag / never-forged-review-marker behaviour. It
#          lives on THIS final task because ALL of it must be wired (review-gate resolution=05, finalize
#          flip=09, exit code=10) for the class to pass — 05/09 carry structural/targeted guardrails only.
#          LOCAL (no scope key) — "the whole feature IS wired" can only be true once this task's attempt has
#          run, so it must NOT run at an intermediate union (#120/#250). Runs ONLY the targeted --filter
#          (never the full integration project — #253 fixture leak). Re-emits failure detail at the END so
#          the WHY reaches the harness feedback tail (#179).
$out = dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~RunOutcomeWiringTests" --nologo 2>&1
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
    Write-Output "run-outcome composition-root proof failing — the proceed-unreviewed run is not fully wired through the real Finalize/RunCommand production path (delivery-suppression / exit-5 / N-unreviewed / no-forged-marker). If the delivery/flag facts fail, the finalize bug is in Scheduler.cs/RunReport.cs (task 09 owns it), not this task's ExitCodes/RunCommand change; escalate rather than editing out of scope. See details above."
    exit 1
}
exit 0
