# catches: a classifier that satisfies the retry rows and silently drops the rest - a switch over the
#          half-dozen statuses an implementer can name rather than the 5xx/3xx BANDS, a cancellation
#          special-case that makes the per-attempt timeout non-retryable, or a redactor built by
#          trimming url.ToString() so it still carries the userinfo. The truth table and the renderer
#          are ONE deliverable: a partial implementation either re-POSTs rows the receiver accepted
#          or prints a live credential into run.log.
# Measured baseline (authoring time): WebhookPolicyTests matches 0 tests in the tree today - the
#          class arrives with task 04 - so the zero-match precondition below is ARMED, not
#          pre-satisfied.
# Discriminating filter (verified at authoring time): "WebhookPolicyTests" is not a substring of
#          "WebhookEventSinkTests" and vice versa, so this selects THIS pair's class and nothing
#          else. Task 07's dispatcher class is deliberately NOT selected here - it does not exist yet
#          at this point in the DAG.
# Boundary: this proves THIS pair green. A regression task 07 might cause in WebhookPolicyTests is
#          caught by the plan-level 02-all-tests-pass guardrail, not by this file.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = "Category=RunEvents&FullyQualifiedName~WebhookPolicyTests"
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
    # being truncated away above the [FAIL] names.
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
    # "System.InvalidOperationException : boom". For this plan's negative redaction tests, Found: IS
    # the finding (#608).
    $emit = $false
    $any = $false
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:') { $emit = $true }
        elseif ($line -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { Write-Output $line; $any = $true }
    }
    if (-not $any) {
        Write-Output "(no failure block matched - the runner's output format may have changed; read the full log above)"
    }
    Write-Output ""
    Write-Output "The tests authored for this deliverable still fail. The assertion detail above is the WHY."
    exit 1
}

$passed = 0; $failed = 0
if ($log -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests - it exits 0 while proving nothing. Either WebhookPolicyTests was renamed or never authored, or its methods lost the Category=RunEvents trait."
    exit 1
}
Write-Output "All $passed test(s) pass for WebhookPolicyTests."
exit 0
