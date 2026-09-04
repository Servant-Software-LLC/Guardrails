# catches: a dispatcher implementation that breaks the two PURE functions living in the same file -
#          and the case that matters is REDACTION. This task rewrites WebhookEventSink.cs around
#          RedactUrl and IsRetryable, and the cheapest way to make a notice read well is to
#          interpolate the URL or the exception message directly instead of routing through
#          RedactUrl. That reintroduces the URL PATH into a notice, and for Slack and webhook.site
#          the path IS the credential - a live leak into a redirected run.log, caused by our own
#          success message. It is invisible to this task's own filter, which selects only
#          WebhookEventSinkTests, so without this guardrail it would surface at the plan-level gate
#          only AFTER tasks 08 and 09 had built on it. The same edit can quietly weaken IsRetryable
#          (a 5xx band collapsed to a switch, a cancellation special-case) with the same delay.
# Measured baseline (authoring time): WebhookPolicyTests matches 0 tests in the tree today - the
#          class arrives with task 04 and is GREEN from task 05 onward - so at this point in the DAG
#          this guardrail is a genuine no-regression check over an already-passing class, and the
#          zero-match precondition below is ARMED, not pre-satisfied.
# Discriminating filter (verified at authoring time): "WebhookPolicyTests" is not a substring of
#          "WebhookEventSinkTests" and vice versa, so this selects the POLICY class only - it does
#          not re-run 01-dispatcher-tests-pass's class, and a failure here is unambiguously a
#          regression rather than unfinished work on this task's own deliverable.
# Ordinal: 02, after 01. Cheapest-first is unchanged - both are the same dotnet test shape over the
#          same project, and this one is second because it is the no-regression check, not the
#          deliverable.
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
    # Microsoft.NET.Test.Sdk 18.6.0). A real failure block for the very regression this guardrail
    # exists to catch looks like this, verbatim:
    #     Failed <FullyQualifiedTestName> [15 ms]
    #     Error Message:
    #      Assert.DoesNotContain() Failure: Sub-string found
    #                                       (a position-marker line)
    #     String: ..." -> https://hooks.example.com/services/T00/B11/XyZ"
    #     Found:  "/services/T00/B11/XyZ"
    #     Stack Trace:
    #        at <TestName>() in <File>:line 13
    # The house allowlist (Error Message|Expected|Actual|Stack Trace|at ) keeps the "Error Message:"
    # HEADER and drops the assertion headline AND the String:/Found: payload - so it would report a
    # leaked-credential regression as an empty "Error Message:" with no indication of WHAT leaked.
    # Found: IS the finding here (#608).
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
    Write-Output "WebhookPolicyTests REGRESSED. These tests were green before this task and they are not this task's deliverable - you broke IsRetryable or RedactUrl while implementing the dispatcher. Do not adjust the tests. If a notice test failed, route the URL through RedactUrl instead of interpolating it, and report the exception TYPE NAME and status only - never ex.Message, which routinely carries the whole request URI."
    exit 1
}

$passed = 0; $failed = 0
if ($log -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests - it exits 0 while proving nothing. Either WebhookPolicyTests was deleted or renamed, or its methods lost the Category=RunEvents trait. A no-regression check over an empty set certifies nothing."
    exit 1
}
Write-Output "No regression: all $passed WebhookPolicyTests test(s) still pass after the dispatcher landed."
exit 0
