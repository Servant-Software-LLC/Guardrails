# catches: a writer that satisfies part of the deliverable and silently drops the rest - a bracket
#          stamped per row instead of once per process, a wire copy that loses byte-identity on the
#          kinds with no detail, a withheld marker where there was nothing to withhold, a cap that
#          never fires (or always does), an onRow invoked OUTSIDE the append lock so enqueue order
#          stops matching file order, or a callback whose throw escapes into a Scheduler worker
#          holding _gate. Those are one deliverable: the delivery key (runId, bracket, seq) and
#          "re-read events.jsonl on a gap" are only safe if every one of them holds.
# It is the SAME set task 02 observed Failed. Nine of the ten flip red -> green here; the tenth,
#          AThrowingOnRowCallbackDoesNotPropagate, was declared-exempt there and must simply stay
#          green - a correct try/catch is what keeps it so.
# Measured baseline (#478): RunEventBracketTests greps to 0 occurrences across src/ and tests/ before
#          task 02 runs, so this filter can only select the tests that task authored - there is no
#          pre-existing class of that name for it to certify instead.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Task-level filters name their own test CLASS; the plan-wide Plan trait is never used alone (#455).
# No -v q on dotnet test: it suppresses the very assertion/exception block re-emitted below (#179).
$filter = "Category=RunEvents&FullyQualifiedName~RunEventBracketTests"
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
    Write-Output "The RunEventBracketTests authored for this deliverable still fail. The assertion detail above is the WHY - fix the writer, never the test (it is outside this task's write scope)."
    exit 1
}

# Zero-match guard on the EXECUTED count (Passed + Failed), never Total, which counts [Skip]ped. A
# --filter that matches nothing exits 0 and would certify an empty set as a green implementation.
$passed = 0; $failed = 0
if ($log -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests - it exits 0 while proving nothing. The class was renamed, deleted, or never authored by task 02."
    exit 1
}

Write-Output "All $passed RunEventBracketTests test(s) pass: bracket is stamped once per process and on every row, and the wire copy matches the file line except for the one documented detail transformation."
exit 0
