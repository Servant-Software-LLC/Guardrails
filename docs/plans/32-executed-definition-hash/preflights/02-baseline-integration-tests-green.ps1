# catches: a plan that builds on ALREADY-RED existing tests in Guardrails.Integration.Tests. This
#          plan's load-bearing pins are integration-only by construction: P2 asserts the WORKTREE-mode
#          write sites (W2/W3), which are the default for a real run and the mode plan 28's motivating
#          overnight run actually used; P3 asserts the Guardrails-Task-Hash trailer on a REAL git
#          segment; P9 - milestone C's acceptance criterion - asserts a green run does not DELIVER. None
#          of those can be faked in a Core unit test (#382). Stages 5, 13 and 15 stake their whole
#          verdict on this project, so a pre-existing red here would be misattributed to them, burn
#          their retry budgets and escalate. "Never build on red" (#181).
#
# THE SHIPPED TRIPWIRE IS DELIBERATELY LEFT INSIDE THIS BASELINE. `PlanEditedDuringRunTests` is NOT
#          excluded, unlike the two classes this plan creates. It ships GREEN today (plan 31 authored
#          it), stage 2 then re-baselines two of its assertions to the post-fix contract, and one of its
#          assertions - AStrayDsStoreMidRun_...'s `Assert.True(report.AllSucceeded)` at :190 - must
#          survive this entire plan UNCHANGED (P16, section 15.1's "one assertion that must NOT move").
#          That is the only thing standing between the new delivery gate and being muted within a week.
#          Proving it green BEFORE the DAG is what makes a later red attributable to the gate rather
#          than to something that was already broken. Excluding it would throw that away for nothing:
#          this preflight runs ONCE, pre-DAG, against the STARTING bytes, where the file is still the
#          shipped version and stage 2 has not run.
#
# SCOPE: the EXISTING Integration tests only, via an FQN exclusion of the two Integration test classes
#        THIS plan creates. A whole-project `dotnet test` here would hit the #165/#176 compile-coupling
#        trap once stage 7 or stage 11 has landed its intentionally-red tests. FQN exclusion, not a
#        plan-wide trait: this plan introduces no trait (shape 3), and a trait names a set while an FQN
#        list names exactly what it excludes.
#
#        Discriminating-substring check (#455 companion (a)): no existing class in this project
#        contains 'MidRunDefinitionEditTests' or 'DivergenceDeliveryGateTests' as a substring. The
#        nearest neighbour by subject is PlanEditedDuringRunTests, which contains neither - which is
#        exactly why the paragraph above works rather than being an aspiration.
#
# Required-present baseline (#478): a POSITIVE precondition on the STARTING tree, green-on-arrival BY
#        DESIGN - the class Step 7.0a exempts. Measured on design/32-executed-definition-hash @1f6d54c:
#        1050 passing, 0 failing, 4 SKIPPED, with the two exclusions below already applied (they match
#        nothing today - neither class exists yet). Those 4 skipped tests are exactly why the guard
#        below sums Passed+Failed and never reads 'Total:'.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED - pin the culture BEFORE the run or the guard inverts (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this preflight is scoped to the Integration test project and cannot run without it."
    exit 1
}

# Stage 7 -> MidRunDefinitionEditTests; stage 11 -> DivergenceDeliveryGateTests. PlanEditedDuringRun
# Tests is NOT excluded - see the block comment above.
$filter = 'FullyQualifiedName!~MidRunDefinitionEditTests' +
          '&FullyQualifiedName!~DivergenceDeliveryGateTests'

# NO -v q on a TEST command (#462/#179).
$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# Zero-match guard (#455): EXECUTED count (Passed + Failed). 'Total:' would count this project's
# [Skip]ped tests, so a run that executed nothing but skipped some would clear a Total-keyed guard.
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
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "The Integration area's EXISTING tests ($executed executed, $failed failed) are already failing on the starting code. Fix the pre-existing breakage first. If the failures are in PlanEditedDuringRunTests, stop and read section 15.1: that file is this plan's own tripwire, stage 2 is about to invert two of its assertions, and a third (AStrayDsStoreMidRun_...'s AllSucceeded at :190) must survive the whole plan untouched - none of which can be read off a suite that was already red."
    exit 1
}

Write-Output "Baseline green: $executed existing Integration tests executed, 0 failed."
exit 0
