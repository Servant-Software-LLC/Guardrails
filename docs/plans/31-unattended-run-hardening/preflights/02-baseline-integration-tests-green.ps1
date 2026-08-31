# catches: a plan that builds on ALREADY-RED existing tests in Guardrails.Integration.Tests. #554's
#          core pins are integration-only by construction - the salvage path is worktree-only
#          (IsRealGitSegment), so a Core-only test with the fake worktree provider passes with the
#          feature entirely absent (plan 31 section 7). Stage 2 therefore stakes its whole verdict on this
#          project, and a pre-existing red here would be misattributed to it, burn its retry budget,
#          and escalate - the exact expensive escalation #554 exists to make cheap. "Never build on
#          red" (#181).
#
# SCOPE: the EXISTING Integration tests only, via an FQN exclusion of the two Integration test classes
#        THIS plan authors. A whole-project `dotnet test` here would hit the #165/#176 compile-coupling
#        trap once stage 1 or stage 7 has landed its intentionally-red tests. FQN exclusion, not a
#        plan-wide trait: a trait names a set, an FQN list names exactly what it excludes.
#
#        Discriminating-substring check (#455 companion (a)): no existing class in this project
#        contains 'EscalationSalvageTests' or 'PlanEditedDuringRunTests' as a substring. The nearest
#        neighbour is RetrySalvageTests - which does NOT contain 'EscalationSalvageTests', so it stays
#        IN this baseline. That is deliberate: RetrySalvageTests is one of the two shipped salvage
#        suites plan 31 section 3.3 requires to keep passing with ZERO edits, and this preflight is what
#        proves it was green before stage 2 touched RetryPolicy.
#
# Required-present baseline (#478): a POSITIVE precondition on the STARTING tree, green-on-arrival BY
#        DESIGN - the class Step 7.0a exempts. Measured on master @1490d2a: 1036 passing, 4 skipped, 0
#        failing. The two excluded classes do not exist yet, so the filter drops nothing today. Note
#        the 4 SKIPPED tests are why the guard below sums Passed+Failed and never reads 'Total:'.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED - pin the culture BEFORE the run or the guard inverts (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this preflight is scoped to the Integration test project and cannot run without it."
    exit 1
}

# Stage 1 -> EscalationSalvageTests; stage 7 -> PlanEditedDuringRunTests.
$filter = 'FullyQualifiedName!~EscalationSalvageTests' +
          '&FullyQualifiedName!~PlanEditedDuringRunTests'

# NO -v q on a TEST command (#462/#179).
$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# Zero-match guard (#455): EXECUTED count (Passed + Failed). 'Total:' would count this project's 4
# [Skip]ped tests, so a run that executed nothing but skipped four would clear a Total-keyed guard.
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "BASELINE FILTER MATCHED NOTHING: 0 tests executed in $project. The exclusion filter is malformed or the test host never ran - this preflight is certifying nothing. Fix the filter before running the plan."
    exit 1
}

if ($code -ne 0) {
    # #179: the WHY has to reach the halt feedback, not just [FAIL] names.
    Write-Output ""
    Write-Output "=== Pre-existing failures in Guardrails.Integration.Tests (detail re-emitted) ==="
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "The Integration area's EXISTING tests ($executed executed, $failed failed) are already failing on the starting code. Fix the pre-existing breakage first - stage 2's entire verdict rests on this project, and RetrySalvageTests in particular must be green BEFORE stage 2 touches RetryPolicy or the zero-edits claim cannot be read."
    exit 1
}

Write-Output "Baseline green: $executed existing Integration tests executed, 0 failed."
exit 0
