# catches: wiring whose BEHAVIOUR deviates from the six conformance clauses this task owns - most
#          expensively the Invariant 7 clause, whose failure means a single-model user who never
#          opted into tiering now gets routed "for uniformity".
#
#          The filter names THIS task's own six clauses by method name (#455). It deliberately does
#          NOT select the three clauses tasks 08/09 own: selecting a clause only a DOWNSTREAM task can
#          green is the forward-deadlock #455 describes - `validate` and `graph --check` both pass
#          (the cycle is between a task and a sibling's test corpus, not between tasks), and the run
#          burns its whole retry budget before landing on needs-human with the deliverable complete.
#          An INCLUSION list is used rather than an exclusion list for exactly that reason: if a
#          method were renamed, this fails loudly on the zero-match guard instead of silently
#          selecting a red clause.
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)

$tests = @(
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

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# and reporting that as "your filter matched nothing" would send the next attempt after the wrong bug.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the attempt-launch wiring does not satisfy DoR section 6. If Invariant7_... is the failing clause, an untagged task in a routing-ENABLED config is being routed instead of taking the legacy path - activation is PLAN-scoped, not config-scoped. If D31_... fails, tierSource is being derived by comparing the tier to the plan default instead of read from ActionDefinition.TierOrigin (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455), keyed to the CLAUSE COUNT rather than to 1: a rename in the conformance
# suite would otherwise leave this passing on a subset. Keyed on the EXECUTED count (Passed+Failed);
# "Total:" counts [Skip]ped tests, so a [Skip]ped clause would clear a Total-keyed guard.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt $tests.Count) {
    Write-Output "exit 0 but only $ran of this task's $($tests.Count) conformance clauses executed - this guardrail certified less than it should. A clause was renamed away from the nine names pinned in task 06's prompt, or is [Skip]ped. Expected: $($tests -join ', ')."
    exit 1
}
exit 0
