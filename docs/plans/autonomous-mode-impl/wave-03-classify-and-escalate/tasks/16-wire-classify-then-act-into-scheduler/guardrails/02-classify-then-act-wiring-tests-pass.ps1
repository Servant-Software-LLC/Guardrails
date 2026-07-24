# catches: the classify-then-act reply channel built but NOT observably wired into the production run
#          path — the #120 false-green (unit-green + terminal-suite-green, yet dead from the real factory).
#          This is a TARGETED specific-tests-pass (#4): it runs the authored SchedulerEscalationWiringTests
#          — which drive the REAL SchedulerFactory.Create (never injecting the seam) — but EXCLUDES the
#          Cli_RunWithUnresolvedEscalation exit-code fact (task 17 adds ExitCodes.EscalationsPending, so
#          that one fact stays RED here by design). The remaining FOUR facts (escalated /
#          proceeded-best-guess-injected / blocker-retried / answer-injected) prove THIS task's deliverable
#          plus task 15's dispatch (this task owns Scheduler.cs too, so a latent 15-dispatch bug is caught
#          AND fixable here — no dead-end at 17). The FULL composition-root proof, incl. the exit-code fact,
#          is the SINK task 17. LOCAL (no scope key) — "the collaborator IS wired" can only be true once
#          this task's attempt has run, so it must NOT run at an intermediate union (#120/#250). Runs ONLY
#          the targeted --filter (not the full integration project — #253 fixture leak). Re-emits failure
#          detail at the END so the WHY reaches the harness feedback tail (#179).
#          The &-exclusion filter is keyed on the fifth fact's distinctive method name; the test file is
#          authored+frozen by task 14, so the name is stable.
$out = dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~SchedulerEscalationWiringTests&FullyQualifiedName!~Cli_RunWithUnresolvedEscalation" --nologo 2>&1
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
    Write-Output "classify-then-act / resume wiring facts failing — escalate / proceeded-best-guess-injection / blocker-retry / resume-answer-injection is not wired through the real SchedulerFactory.Create + Scheduler (see details above). Do NOT weaken the test; wire the behaviour."
    exit 1
}
exit 0
