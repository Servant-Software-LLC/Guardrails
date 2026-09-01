# catches: anything this plan broke, anywhere. This is the FIRST unfiltered tests-pass in the plan
#          (section 15) and the last stage before the documentation one, so it is where the sixteen-stage
#          chain finally has to hold together at once.
#
#          Three specific things it is watching for, all of which the filtered guardrails upstream could
#          not see:
#            - the five PlanEditedDuringRunTests facts. Two were inverted at stage 2, two more at stage
#              14, and one - AStrayDsStoreMidRun_...'s AllSucceeded assertion, P16 - must have survived
#              the entire plan UNTOUCHED. All five are green here or this stage is not done.
#            - the advisory string. Section 15.1: the cheapest green ships a harness printing "Nothing was
#              halted and nothing was re-run" beside exit 2 and a blocked delivery. Stage 14 authored the
#              assertions that make that impossible; this is where they are collected.
#            - everything else in both suites, because a change to the single delivery predicate reaches
#              every consumer (section 6.5 traces all seven).
#
#          The filter is written as an exclusion that matches nothing, so it selects the whole project
#          while keeping the two-suite shape and the executed-count guard below intact.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179).
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED (a German-culture box prints 'gesamt:' and no
# 'Total:'), which would invert the guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# UNFILTERED, both projects - the FIRST unfiltered tests-pass in this plan, and the reason section 15
# says so explicitly. Stages 3-13 ran filtered because stage 2's re-baseline left PlanEditedDuringRun
# Tests legitimately red until its implementers landed; stage 14 authored the last two rewrites and
# this stage lands the code that makes them green, so from here the whole suite is the honest check.
# There is no --filter at all, which also means no zero-match hole from a mistyped class name - but
# the executed-count guard stays, because a test host that starts and runs nothing still exits 0.
$suites = @(
    @{ Project = 'tests/Guardrails.Core.Tests'
       Filter  = 'FullyQualifiedName!~ThisFilterMatchesNothingAndSelectsEverythingElse'
       Hint    = 'The whole Core suite must be green on this stage. If ExecutedDefinitionDivergenceTests or ExecutedDefinitionHashAnchorTests is red, this stage regressed something stages 6 and 13 established.' }
    @{ Project = 'tests/Guardrails.Integration.Tests'
       Filter  = 'FullyQualifiedName!~ThisFilterMatchesNothingAndSelectsEverythingElse'
       Hint    = 'The whole Integration suite must be green on this stage - including all five PlanEditedDuringRunTests facts, which have carried legitimate red since stage 2. If ARunCarryingOnlyAPlanEditObservation_HaltsWithExitTwoAndDoesNotDeliver is red, the exit-code branch did not land. If TheRenderedText_CarriesAllThreeSection51Consequences is red, the advisory still claims the POST-edit hash is recorded or still says nothing was halted - both false after this plan, on the exact surface it exists to make honest. If AStrayDsStoreMidRun_... is red, the delivery gate has become noisy: that is P16 and it is the only thing standing between the gate and being muted within a week.' }
)

# ACCUMULATE (#478): one distinguishable message per suite, dumped once at the end.
$failures = @()

foreach ($suite in $suites) {
    # NO -v q on a TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
    # leaving only the [FAIL] line for the re-emit below to find - defeating #179 by the flag alone
    # (#462).
    $out = & dotnet test $suite.Project --filter $suite.Filter --nologo 2>&1
    $testExit = $LASTEXITCODE                              # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }

    # EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero
    # with no summary at all, so checking the exit code first reports its real error instead of blaming
    # the filter - a confident misdiagnosis pointing at the one artifact a retry agent may NOT edit here.
    if ($testExit -ne 0) {
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 40                        # bound the block so it fits the ~60-line tail
        Write-Output ""
        Write-Output "=== $($suite.Project) failure details (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
        $failures += "$($suite.Project) is red under filter '$($suite.Filter)'. $($suite.Hint)"
        continue
    }

    # ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
    # or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed); 'Total:' would also count
    # [Skip]ped tests, so a fully-skipped selection would clear a Total-keyed guard.
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failures += "$($suite.Project) exited 0 but executed ZERO tests under filter '$($suite.Filter)' - this guardrail certified nothing. The filter matched no tests, is malformed, or every match is [Skip]ped."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== $($failures.Count) suite(s) not green ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Full suites green on both projects - including every PlanEditedDuringRunTests fact, which has carried legitimate red since stage 2."
exit 0
