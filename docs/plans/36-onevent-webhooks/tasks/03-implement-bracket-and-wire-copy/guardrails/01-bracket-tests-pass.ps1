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
# FORWARD PER-TEST CENSUS (#375), added because the exit code cannot carry this. The suite exit code
#          alone cannot tell a behaviour that PASSED from one that was never merged in or was
#          [Skip]ped out - a LOST TEST READS AS GREEN TO AN EXIT CODE. A merge that drops 3 of
#          RunEventBracketTests' 10 methods leaves 7 executed, exit 0, and this guardrail green over a
#          bracket implementation nothing now pins. This task's writeScope is
#          src/Guardrails.Core/Execution/ only, so its own agent cannot delete a test - but a merge
#          can, and the census below is what notices. It is the exact mirror of task 09's
#          guardrails/02-webhook-delivery-tests-pass.ps1, which states the same reasoning for the
#          integration half; the two must not diverge in strength.
# ALL TEN ARE REQUIRED 'Passed' HERE, including AThrowingOnRowCallbackDoesNotPropagate. Its
#          exemption in task 02 was from the RED bar and is not inherited: there, the stub never
#          invokes onRow so nothing can throw and neither Failed nor Passed distinguishes a good test
#          from a bad one. Here the try/catch design section 3.1 pins EXISTS, so Passed is the honest
#          requirement - exactly as task 09 treats task 08's declared exemption
#          AReceiverThatNeverBindsLeavesExitCodeUntouched. The two manifests name the same ten methods
#          and must stay in lockstep.
# Measured baseline (#478): RunEventBracketTests greps to 0 occurrences across src/ and tests/ before
#          task 02 runs, so this filter can only select the tests that task authored - there is no
#          pre-existing class of that name for it to certify instead.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Task-level filters name their own test CLASS; the plan-wide Plan trait is never used alone (#455).
# No -v q on dotnet test: it suppresses the very assertion/exception block re-emitted below (#179).
$filter = "Category=RunEvents&FullyQualifiedName~RunEventBracketTests"
# TRX for the forward census at the foot of this file. Keyed on $PID and cleared BEFORE the run, so a
# previous attempt's results can never be read as this attempt's (the same shape task 09's forward
# census uses). NO --no-build, deliberately: with it the runner reads whatever is in bin/ rather than
# the source tree, and a stale assembly can carry tests whose source file is gone.
$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr36-bracket-forward-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue
$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj `
    --filter $filter --nologo --logger "trx;LogFileName=forward.trx" --results-directory $resultsDir 2>&1 | Out-String
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

# ============ THE FORWARD PER-TEST CENSUS (#375) - see the header for why the exit code is not enough.
# PRECONDITION: no TRX means the census cannot run at all, and a census that cannot run must never be
# silently skipped - that is the failure mode this whole block exists to close.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "PRECONDITION: no .trx under $resultsDir - the run produced no results file, so the per-test census below could not be evaluated. $passed test(s) reported passing is NOT sufficient: this guardrail certifies nothing without the census."
    exit 1
}

# DOTTED navigation - the TRX carries a default xmlns, so SelectNodes('//UnitTestResult') finds
# nothing. The `| Where-Object { $_ }` is LOAD-BEARING: a TRX with no <Results> element yields $null,
# and @($null).Count is 1, so the bare @(...) form could never fire (#455).
$trxXml   = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($trxXml.TestRun.Results.UnitTestResult | Where-Object { $_ })

# The ten methods task 02's action prompt PINS, cross-checked by hand against that task's own red
# manifest in guardrails/02-tests-fail-on-stubs.ps1. Nine of them are its $mustFail set; the tenth,
# AThrowingOnRowCallbackDoesNotPropagate, is its declared RED exemption and is NOT exempt here (see
# the header). Every one must be observed Passed.
$mustPass = @(
    'BracketIsPresentOnEveryRow',
    'BracketMatchesUnixMillisAndFourHex',
    'BracketIsStableAcrossRowsInOneStream',
    'BracketDiffersAcrossTwoStreams',
    'WireLineEqualsFileLineWhenDetailIsNull',
    'WireLineEqualsFileLineForPassingGuardrailFinished',
    'WireLineCarriesWithheldMarkerWhenDetailPresent',
    'WireLineCapsDetailAtMaxCharsWhenIncludeDetailIsTrue',
    'SeqAndBracketStayConsistentUnderConcurrentWriters',
    'AThrowingOnRowCallbackDoesNotPropagate'
)

$census = New-Object System.Collections.Generic.List[string]
foreach ($name in $mustPass) {
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not. The (\(|$) tail admits
    # a [Theory] row's appended data without admitting a longer sibling name, and the leading `\.`
    # anchors on the method segment of "Namespace.Class.Method". Mirrors task 09's forward census.
    # KNOWN ASYMMETRY, deliberate: task 02's RED census matches case-INSENSITIVELY, so a method spelled
    # in the wrong case would pass there and land here - on a task whose write scope cannot reach the
    # test file. That degrades HONESTLY rather than into a retry loop: the finding below names the
    # method and instructs escalation, never authoring. Do not "fix" it by weakening this to -match.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $census.Add("[$name] NO RECORD - no test with this method name ran. The suite exiting 0 does not mean this behaviour is proven; it means nothing asserted it. This test is OUTSIDE this task's write scope: do NOT author it here. If it genuinely did not arrive, that is a delivery problem - escalate with {`"needsHuman`": {`"question`": `"...`", `"kind`": `"blocked-work`"}}.")
        continue
    }
    $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notGreen.Count -gt 0) {
        $seen = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $census.Add("[$name] $($notGreen.Count) of $($hits.Count) record(s) reported '$seen', not 'Passed'. ('NotExecuted' = [Fact(Skip=...)] or a skipped [Theory] row - a skipped regression guard guards nothing.)")
    }
}

if ($census.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Forward per-test census: $($census.Count) finding(s) across $($mustPass.Count) enumerated behaviours ==="
    $census | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The suite exited 0 and $passed test(s) passed, but the behaviours above are NOT among them. A test that was never merged in, renamed, or [Skip]ped reads exactly like a passing one to an exit code; this census is what tells them apart."
    exit 1
}

Write-Output "All $passed RunEventBracketTests test(s) pass and all $($mustPass.Count) enumerated behaviours are bound to an OBSERVED PASSING test: bracket is stamped once per process and on every row, and the wire copy matches the file line except for the one documented detail transformation."
exit 0
