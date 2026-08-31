# catches: a watch that is implemented but never reaches a real run. Task 08's unit suite drives the
#          watch directly and goes fully green over a Scheduler that never constructs it, never polls
#          it and never re-baselines it - so these five Integration pins are the ONLY place the
#          feature is proven through the path an operator actually exercises.
#
#          The two that matter most are the ones a wrong implementation passes by accident:
#          P2 (a JIT wave breakdown emits ZERO plan-edit entries) fails if the five plan-wide
#          re-baseline hooks are missing - and a watch that reports the harness's own writes as
#          operator edits gets muted within a week (#229), which kills the feature more surely than
#          not shipping it. P3 (an observation is outcome-INERT: the run still fast-forwards and still
#          exits 0, not 5) fails if the decision token was spelled as something RunOutcomePolicy's
#          SuppressesDelivery or ProceededUnreviewedWaveCount predicate recognises.
#
#          Re-emits the assertion detail at the END so the WHY reaches the harness retry-feedback tail
#          (#179) - default `dotnet test` prints it mid-run and ends with only [FAIL] plus a count.
#
# BOTH classes, deliberately: LivePlanEditWatchTests was green when task 08 settled, and this task's
#        four files must not have broken it (a shape change to PlanEdit's consumers is the obvious
#        way). Re-running it here attributes that regression to THIS task rather than to the terminal
#        gate hours later.
#
# The filters name the OWN test classes of the two tasks in this chain (#455). The plan introduces no
# plan-wide trait, so each class term stands alone - shape 3 of the four sanctioned forms, not an
# omission. Both are discriminating: no other class in either project contains
# 'LivePlanEditWatchTests' or 'PlanEditedDuringRunTests'.
$ErrorActionPreference = 'Continue'

# The run summary the zero-match guard reads is LOCALIZED - pin the culture FIRST (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$suites = @(
    @{ Project = 'tests/Guardrails.Integration.Tests'
       Filter  = 'FullyQualifiedName~PlanEditedDuringRunTests'
       Hint    = 'the five section 5.5 pins. If P2 fails, the five plan-wide Rebaseline() hooks are missing or incomplete - and only P2 own writer (the JIT wave breakdown) has a pin at all, so read the other four yourself. If P3 fails, the decision token is not spelled observed. If P5 fails, the rendered text is missing one of the three section 5.1 consequences - and "your edit was ignored" is FALSE, so do not write it.' },
    @{ Project = 'tests/Guardrails.Core.Tests'
       Filter  = 'FullyQualifiedName~LivePlanEditWatchTests'
       Hint    = 'task 08 unit suite, which was GREEN when it settled. A failure here is a regression THIS task introduced - most likely a shape change to PlanEdit / PlanEditedFile / PlanEditKind, which plan section 5.2 pins. LivePlanEditWatch.cs is outside your writeScope; fix your consumers, not the watch.' }
)

# ACCUMULATE: one message per broken suite, dumped once at the end.
$failures = @()

foreach ($suite in $suites) {
    # NO -v q on a TEST command: it deletes the Error Message / Expected / Actual / Stack Trace block
    # the re-emit below exists to surface, defeating #179 by the flag alone (#462).
    $out = & dotnet test $suite.Project --filter $suite.Filter --nologo 2>&1
    $testExit = $LASTEXITCODE                      # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }

    # EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero
    # with no summary at all. Guard-first would swallow that and report "the filter matched ZERO tests
    # - check the class name", a confident misdiagnosis pointing at files this task may not edit.
    if ($testExit -ne 0) {
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 40                # bound the block so it fits the ~60-line tail
        Write-Output ""
        Write-Output "=== $($suite.Project) failure details (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
        $failures += "$($suite.Project) is RED. $($suite.Hint)"
        continue
    }

    # ZERO-MATCH GUARD (#455): exit 0 alone does not mean tests passed - a --filter matching nothing,
    # or a malformed one, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would
    # also count [Skip]ped tests, and the Integration project already carries skipped tests.
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failures += "$($suite.Project): exit 0 but ZERO tests executed. The --filter '$($suite.Filter)' matched nothing, is malformed, or every matched test is [Skip]ped - this guardrail certified nothing. The classes are task 07's deliverable; if one is genuinely absent, escalate rather than writing it."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== plan-edit wiring: $($failures.Count) suite(s) not green ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Plan-edit wiring green: the five section 5.5 run pins pass, and task 08's unit suite is still green."
exit 0
