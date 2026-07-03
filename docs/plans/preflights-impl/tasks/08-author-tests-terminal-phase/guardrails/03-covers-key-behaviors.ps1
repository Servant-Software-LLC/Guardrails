# catches: a terminal-phase test file that satisfies build + tests-fail-on-current-code with one trivial
#          test while skipping revalidate / terminal-only-resume / serial+worktree coverage. Lower-bound
#          presence grep, scoped to the one file this task owns. One `if` per token so the failure names the gap.
$f = Get-Content "tests/Guardrails.Integration.Tests/PlanGuardrailPhaseTests.cs" -Raw
if ($f -notmatch 'planGuardrails|PlanGuardrails') {
    Write-Output "PlanGuardrailPhaseTests.cs does not reference the planGuardrails marker - the terminal halt behavior is untested"
    exit 1
}
if ($f -notmatch 'plan:guardrails') {
    Write-Output "PlanGuardrailPhaseTests.cs does not exercise --revalidate-task plan:guardrails (B2(a), #6)"
    exit 1
}
if ($f -notmatch '(?i)resume|skip') {
    Write-Output "PlanGuardrailPhaseTests.cs does not cover B2(b) terminal-only resume (#5)"
    exit 1
}
if ($f -notmatch 'MaxParallelism|Parallelism|worktree|Worktree') {
    Write-Output "PlanGuardrailPhaseTests.cs does not appear to exercise BOTH serial and worktree mode"
    exit 1
}
exit 0
