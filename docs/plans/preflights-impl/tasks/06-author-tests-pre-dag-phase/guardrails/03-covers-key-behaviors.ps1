# catches: a pre-DAG phase test file that satisfies build + tests-fail-on-current-code with ONE trivially-
#          failing test while skipping the resume-SKIP scenario or serial/worktree coverage. Lower-bound
#          presence grep (a comment still matches; residual is the red + human review), scoped to the one
#          file this task owns. One `if` per token so the failure names the gap.
$f = Get-Content "tests/Guardrails.Integration.Tests/PlanPreflightPhaseTests.cs" -Raw
if ($f -notmatch 'planPreflights|PlanPreflights') {
    Write-Output "PlanPreflightPhaseTests.cs does not reference the planPreflights marker - the halt/marker behavior is untested"
    exit 1
}
if ($f -notmatch '(?i)resume|SKIP') {
    Write-Output "PlanPreflightPhaseTests.cs does not cover the B1 resume-SKIP scenario (#2) - the negative-baseline-not-re-run behavior is untested"
    exit 1
}
if ($f -notmatch '(?i)fresh') {
    Write-Output "PlanPreflightPhaseTests.cs does not cover the --fresh re-run scenario (#3)"
    exit 1
}
# serial + worktree coverage (both MaxParallelism values must appear)
if ($f -notmatch 'MaxParallelism|Parallelism|worktree|Worktree') {
    Write-Output "PlanPreflightPhaseTests.cs does not appear to exercise BOTH serial and worktree mode - the serial-mode IReVerifier wiring is where a false-green hides"
    exit 1
}
exit 0
