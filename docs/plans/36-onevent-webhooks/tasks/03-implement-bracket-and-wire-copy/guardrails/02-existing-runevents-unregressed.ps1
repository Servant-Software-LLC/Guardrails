# catches: a bracket/wire-copy implementation that satisfies the NEW tests while breaking an existing
#          RunEventStream row - a field renamed on the way into the `with` expression, a kind that
#          stops being emitted, `at` or `seq` moved out of the append lock while the bracket was moved
#          in, a serializer option changed so omitted-when-null stops holding. Task 03's own guardrail
#          filters on RunEventBracketTests and structurally CANNOT see any of that: it selects only
#          the tests task 02 authored. Without this check the regression surfaces at the plan-level
#          gate instead - after tasks 04 through 09 have already built on top of it, which is the
#          expensive end of the "never build on red" lesson (#181) rather than the cheap one.
# Plan!=36-onevent is the ONE legitimate use of the plan-wide trait outside the baseline preflights,
#          and it is legitimate because it is an EXCLUSION, not a selector. This check must assert on
#          the EXISTING area tests only; task 02's intentionally-red tests live in this same segment
#          and carry Plan=36-onevent precisely so they can be excluded here. Selecting on the bare
#          trait would be the #455 violation - excluding on it is what makes this check possible at
#          all. Do not "fix" this into a class-named filter: naming a class is what the task's OWN
#          guardrail does, and the whole point of this one is that it names none of them.
# Measured baseline (#478): 49 Core Category=RunEvents tests pass on this branch before the plan runs
#          (re-measured by the coordinator; plan 35's own comment says 41 and is stale). Green on
#          arrival is CORRECT here - this is the named `tests-untouched` regression exemption, an
#          assert-the-existing-behaviour-still-holds check rather than a work guardrail, so a nonzero
#          count is the point and not a pre-satisfied clause.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# No -v q on dotnet test: it suppresses the very assertion/exception block re-emitted below (#179).
$filter = "Category=RunEvents&Plan!=36-onevent"
$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj `
    --filter $filter --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

# Forward polarity: exit code FIRST, so a test host that never ran is reported as the failure it is
# rather than being misread as a bad filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Failure detail (#179 re-emit: the WHY, at the END of stdout where the retry-feedback tail reaches it) ==="

    # ANCHOR + WINDOW, not a line-by-line filter. xunit prints the assertion sentence itself
    # ("Assert.True() Failure", "Assert.Equal() Failure: Values differ") on the line AFTER
    # "Error Message:", and it is unpredictably shaped - a per-line pattern set drops exactly the
    # sentence that says WHY while dutifully re-emitting the labels around it. Measured on a
    # synthesized failing log before this guardrail was committed (#302).
    $lines = @($log -split "`r?`n")
    $keep = New-Object 'bool[]' $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*(Failed|Error Message|Stack Trace)' -or $lines[$i] -match '\[FAIL\]') {
            for ($j = $i; $j -lt [Math]::Min($i + 8, $lines.Count); $j++) {
                $keep[$j] = $true
            }
        }
    }
    $emitted = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($keep[$i] -and $lines[$i].Trim().Length -gt 0) {
            if ($emitted -ge 120) {
                Write-Output "... re-emit capped at 120 lines; the full runner output is above."
                break
            }
            Write-Output $lines[$i]
            $emitted++
        }
    }

    Write-Output ""
    Write-Output "An EXISTING Core RunEvents test broke. These tests pin the rows plans 34 and 35 shipped, and bracket is additive - nothing in this task's brief changes an existing field, kind or omission rule. The assertion detail above names which one moved. Fix the writer so both the new tests and these hold; never adjust an existing test to match a new implementation (they are outside this task's write scope in any case)."
    exit 1
}

# Zero-match guard on the EXECUTED count (Passed + Failed), never Total, which counts [Skip]ped. A
# --filter that matches nothing exits 0 and would certify an empty set as an unregressed area.
$passed = 0; $failed = 0
if ($log -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests - it exits 0 while proving nothing. The Category trait, the Plan trait spelling or the test project moved, or every existing RunEvents test was deleted. Baseline at authoring time was 49."
    exit 1
}

Write-Output "No regression: $passed existing Core RunEvents test(s) still pass (baseline 49; this plan's own red is excluded by Plan!=36-onevent). The kinds and fields plans 34 and 35 shipped keep their exact shape."
exit 0
