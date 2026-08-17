# catches: a no-route settle whose behaviour deviates from the clause this task owns, AND a
#          regression in the six clauses task 07 made green. The regression half is not defensive
#          padding: tasks 07, 08 and 09 all edit TaskExecutor.cs in a CHAIN, so a change here that
#          breaks 07's provenance write would otherwise go green and only red-halt at the wave exit
#          gate - which is a gate, not a task: no retry budget, and the failure attributed to the
#          wave instead of to the change that caused it.
#
#          Task 07 is an ANCESTOR, so its clauses are already green in this tree - selecting them
#          reintroduces no #455 forward-deadlock (there is no downstream task to wait on and no
#          concurrent sibling to race). The two clauses task 09 owns are deliberately NOT selected.
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)

$tests = @(
    'NoCandidateAtOrAboveRung_SettlesNoRoute_AsNeedsHuman',
    'Resolution_RunsPerAttempt_AndReachesAttemptProvenance',
    'ResolverCandidacy_AgreesWith_ServesTier_Predicate',
    'Invariant7_RoutingEnabledConfig_ZeroTagPlan_UsesLegacyPath_WithNoTierActivity',
    'D30_TieredPlan_ClimbsToStrongerRung_AndNeverFallsBackToLegacy',
    'D31_FullPin_RecordsTierSourceOverride_WithProvenanceTierAbsent',
    'Climb_ToStrongerRung_IsRecordedInProvenance'
)
$filter = ($tests | ForEach-Object { "FullyQualifiedName~Stage2ConformanceTests.$_" }) -join '|'

# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455).
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "no-route is not settled to DoR 6.2/12.4, or this change regressed a clause task 07 had green. If NoCandidateAtOrAboveRung_... fails on the runner having been invoked, the settle is happening AFTER an attempt launched - it must happen BEFORE (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455), keyed to the CLAUSE COUNT. Keyed on the EXECUTED count (Passed+Failed);
# "Total:" counts [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt $tests.Count) {
    Write-Output "exit 0 but only $ran of $($tests.Count) selected conformance clauses executed - this guardrail certified less than it should. A clause was renamed away from the nine names pinned in task 06's prompt, or is [Skip]ped. Expected: $($tests -join ', ')."
    exit 1
}
exit 0
