# catches: a dispatcher that satisfies the easy half and silently drops the rest - the single failure
#          the whole feature turns on is a teardown that tears the transport down before the pump has
#          returned, or one that abandons the terminal row to the circuit or to a backlog, so
#          run-finished lands in events.jsonl and never reaches the wire. Plan 35 section 9.3 measured
#          that exact shape at 0% effective across ~10 variants, and a 0%-effective best-effort
#          mechanism is dead code. A DisposeAsync that throws is the other half: it replaces the
#          in-flight `return exitCode;` and turns a wholly green run into an unhandled exception.
# Measured baseline (authoring time): WebhookEventSinkTests matches 0 tests in the tree today - the
#          class arrives with task 06 - so the zero-match precondition below is ARMED, not
#          pre-satisfied.
# Discriminating filter (verified at authoring time): "WebhookEventSinkTests" is not a substring of
#          "WebhookPolicyTests" and vice versa, so this selects THIS pair's class and nothing else.
# Boundary: this proves THIS pair green. This task rewrites the same production file tasks 04/05
#          own, so a regression in WebhookPolicyTests is possible - it is caught by the plan-level
#          02-all-tests-pass guardrail, deliberately not re-run here, since a task guardrail that
#          sweeps another pair's class misattributes that pair's failure to this task.
# FORWARD PER-TEST CENSUS (#375), added because the exit code cannot carry this. The suite exit code
#          alone cannot tell a behaviour that PASSED from one that was never merged in or was
#          [Skip]ped out - a LOST TEST READS AS GREEN TO AN EXIT CODE. That is the sharpest risk in
#          the whole plan HERE: the two teardown behaviours (TerminalRowIsAttemptedWithTheCircuitOpen,
#          TerminalRowIsAttemptedWithABacklogPending) are the ones plan 35 section 9.3 measured at 0%
#          effective across ~10 variants, so losing exactly those two to a merge leaves twelve green
#          tests certifying a dispatcher whose single most important behaviour is unpinned. This
#          task's writeScope cannot reach the test file, so its own agent cannot delete a test; a
#          merge can. It is the exact mirror of task 09's
#          guardrails/02-webhook-delivery-tests-pass.ps1, which states the same reasoning for the
#          integration half; the two must not diverge in strength. The fourteen names below are task
#          06's red manifest verbatim (guardrails/02-tests-fail-on-stubs.ps1) - red there, Passed
#          here - and the two manifests must stay in lockstep.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = "Category=RunEvents&FullyQualifiedName~WebhookEventSinkTests"
# TRX for the forward census at the foot of this file. Keyed on $PID and cleared BEFORE the run, so a
# previous attempt's results can never be read as this attempt's (the same shape task 09's forward
# census uses). NO --no-build, deliberately: with it the runner reads whatever is in bin/ rather than
# the source tree, and a stale assembly can carry tests whose source file is gone.
$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr36-dispatcher-forward-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue
$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo `
    --logger "trx;LogFileName=forward.trx" --results-directory $resultsDir 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

# Forward polarity: exit code FIRST (so a test host that never ran is not misreported as a bad
# filter), then the zero-match guard on the EXECUTED count - never Total, which counts [Skip]ped.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Failures ==="
    # #179 re-emit: the assertion / exception / stack-trace detail is re-printed at the END of stdout
    # so the WHY survives into the ~60-line retry-feedback tail the agent actually reads, instead of
    # being truncated away mid-run. Note [FAIL] is NOT dead - xunit.v3 emits both
    # "[xUnit.net ...] <FQN> [FAIL]" diagnostics and "  Failed <FQN> [15 ms]" block headers, so test
    # NAMES were never the thing that went missing. What went missing is below.
    # BLOCK capture rather than a line-pattern allowlist, and the difference was MEASURED against
    # this repo's exact stack (net10.0, xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.5,
    # Microsoft.NET.Test.Sdk 18.6.0). A real failure block is:
    #     Failed <FullyQualifiedTestName> [15 ms]
    #     Error Message:
    #      Assert.DoesNotContain() Failure: Sub-string found
    #                                       (a position-marker line)
    #     String: ..." -> https://hooks.example.com/services/T00/B11/XyZ"
    #     Found:  "/services/T00/B11/XyZ"
    #     Stack Trace:
    #        at <TestName>() in <File>:line 13
    # The house allowlist (Error Message|Expected|Actual|Stack Trace|at ) keeps the "Error Message:"
    # HEADER and drops every line that says what actually went wrong: the assertion headline, the
    # String:/Found: payload, and a THROWN test's only detail line, which is of the form
    # "System.InvalidOperationException : boom". That last one is this task's common case - a
    # half-built dispatcher fails by throwing, not by asserting (#608).
    $detail = @()
    $emit = $false
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:') { $emit = $true }
        elseif ($line -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40             # bound it so the block fits the ~60-line tail
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else {
        Write-Output "(no failure block matched - the runner's output format may have changed; read the full log above)"
    }
    Write-Output ""
    Write-Output "The tests authored for this deliverable still fail. The assertion detail above is the WHY. If a teardown test fails, re-read section 3.3: signal wind-down first, drain second, tear the transport down last."
    exit 1
}

$passed = 0; $failed = 0
if ($log -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests - it exits 0 while proving nothing. Either WebhookEventSinkTests was renamed or never authored, or its methods lost the Category=RunEvents trait."
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

# Task 06's red manifest, verbatim. The two teardown rows the header singles out are
# TerminalRowIsAttemptedWithTheCircuitOpen and TerminalRowIsAttemptedWithABacklogPending.
$mustPass = @(
    'BackoffScheduleIsOneTwoFourWithJitter',
    'PerRowCeilingIsFortyFiveSeconds',
    'CircuitOpensAtExactlyFiveConsecutiveFailures',
    'CircuitNeverCloses',
    'FullQueueDropsTheOldestNotTheNewest',
    'EveryDroppedRowIsCounted',
    'DisposeAsyncNeverThrowsWhenTheNoticeSinkThrows',
    'DisposeAsyncNeverThrowsWhenTheTransportThrows',
    'TerminalRowIsAttemptedWithTheCircuitOpen',
    'TerminalRowIsAttemptedWithABacklogPending',
    'CancelledPathUsesTheShortBudget',
    'AFaultedPumpIsReportedNotSummarizedAsZero',
    'NoNoticeTextEverContainsTheAuthValue',
    'NoNoticeTextEverContainsTheUrlPath'
)

$census = New-Object System.Collections.Generic.List[string]
foreach ($name in $mustPass) {
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not. The (\(|$) tail admits
    # a [Theory] row's appended data without admitting a longer sibling name, and the leading `\.`
    # anchors on the method segment of "Namespace.Class.Method". The anchoring matters concretely
    # here: an unanchored substring match would let CircuitNeverClosesAfterACooldown stand in for
    # CircuitNeverCloses, which is a DIFFERENT and weaker claim. Mirrors task 09's forward census.
    # KNOWN ASYMMETRY, deliberate: task 06's RED census matches case-INSENSITIVELY, so a method spelled
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
    Write-Output "The suite exited 0 and $passed test(s) passed, but the behaviours above are NOT among them. A test that was never merged in, renamed, or [Skip]ped reads exactly like a passing one to an exit code; this census is what tells them apart. If a TerminalRowIsAttempted* row is missing, the single behaviour section 3.3 turns on is unpinned."
    exit 1
}

Write-Output "All $passed test(s) pass for WebhookEventSinkTests, and all $($mustPass.Count) enumerated behaviours are bound to an OBSERVED PASSING test - the two terminal-row teardown behaviours included."
exit 0
