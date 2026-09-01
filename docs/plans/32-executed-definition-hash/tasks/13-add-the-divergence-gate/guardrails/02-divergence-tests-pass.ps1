# catches: the two opposite ways this gate can be wrong, and section 15.4 exists because an earlier
#          draft could only see one of them.
#
#          TOO QUIET: the gate never fires. Stage 10's P12 (the two-sided JIT-breakdown pin) and P15 (the
#          provenance discriminator) were RED before this stage and must be green now. P15 is the one
#          that matters most - milestone C is fully satisfiable by driving the flag from the plan-edit
#          watch's MOVING baseline, which passes P9 through P13 while shipping a different mechanism
#          under this plan's name.
#
#          TOO NOISY: the gate compares the FULL surface instead of the ignore-list-filtered one. That is
#          a three-line wrong implementation which passes P9 through P15 and every other guardrail in
#          this plan, and it blocks an overnight run's delivery on a .DS_Store, a Thumbs.db, or a .swp
#          left by an operator who opened a guardrail to READ it. Section 6.2 calls the consequence
#          plainly: "A delivery gate that does that is disabled within a week, and then the real signal
#          is gone too." The shipped AStrayDsStoreMidRun_... assertion is the only thing that catches it,
#          this is the ONLY stage whose implementation can turn it red, and section 15.4 forbids
#          filtering it out here.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179).
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED (a German-culture box prints 'gesamt:' and no
# 'Total:'), which would invert the guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# TWO entries, and the SECOND one is section 15.4's explicit exception - it is not optional.
#   1. The whole Core divergence suite. All four of stage 10's pins must now be green: P12 and P15
#      were red (there was no gate), P10 and P16 were green and must stay green.
#   2. ONE integration method by name: AStrayDsStoreMidRun_EmitsNothingWhileTheDefinitionHashStill
#      Changes. Section 15.4: 'Stage 13 is the stage that builds the gate. It is therefore the only
#      stage whose implementation can turn that test red - by comparing the full surface instead of
#      the filtered one, which is a three-line wrong implementation that passes P9 through P15 and
#      every other guardrail in this plan. Filtering it out of the one stage that can trip it is why
#      an earlier draft's tripwire caught nothing.'
#      Its other four methods stay filtered out until stage 15 - two of them assert on an advisory
#      string and an exit code only stage 15 changes, and would fail here for a reason this stage
#      cannot fix.
$suites = @(
    @{ Project = 'tests/Guardrails.Core.Tests'
       Filter  = 'FullyQualifiedName~ExecutedDefinitionDivergenceTests'
       Hint    = 'If P12 or P15 failed, the gate does not fire - or it fires from the WRONG SOURCE. P15 discriminates on PROVENANCE: after a mid-run edit the plan-edit watch has ALREADY reported and re-baselined on, the settling task must STILL diverge, which only a PINNED baseline survives. If P10 or P16 failed, the gate is too NOISY: it must compare the IGNORE-LIST-FILTERED surface using the one shared predicate, never the full surface.' }
    @{ Project = 'tests/Guardrails.Integration.Tests'
       Filter  = 'FullyQualifiedName~PlanEditedDuringRunTests.AStrayDsStoreMidRun_EmitsNothingWhileTheDefinitionHashStillChanges'
       Hint    = 'THIS IS THE TRIPWIRE (section 15.4, P16). A mid-run stray .DS_Store must leave the run GREEN AND DELIVERING while the RECORDED hash still differs from disk. If it is red, the gate is comparing the FULL surface instead of the ignore-list-filtered one - a three-line wrong implementation that passes every other pin in this plan, and one that would see the delivery gate disabled within a week (section 6.2 / #229). Apply LivePlanEditWatch.IsEditorArtifact - stage 5 promoted it to internal for exactly this - to BOTH sides before diffing. The RECORDED hash keeps the full unfiltered surface: do not touch HashText.' }
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
Write-Output "Divergence gate verified: the Core divergence suite is green and the stray-artifact tripwire still leaves the run green and delivering."
exit 0
