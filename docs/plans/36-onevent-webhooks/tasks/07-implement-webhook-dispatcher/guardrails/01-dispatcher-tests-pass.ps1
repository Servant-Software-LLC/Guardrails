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
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = "Category=RunEvents&FullyQualifiedName~WebhookEventSinkTests"
$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo 2>&1 | Out-String
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
Write-Output "All $passed test(s) pass for WebhookEventSinkTests."
exit 0
