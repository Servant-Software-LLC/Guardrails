# catches: a capture whose behaviour deviates from the four behaviours THIS task pair owns - most
#          sharply, the two that are easy to get half-right: lifting the fingerprint in ApplyChunk and
#          not in its ApplyWholeCompletion twin (a server that ignores "stream": true then silently
#          reports no digest), and filling an ABSENT fingerprint with "" or with the model tag, which
#          turns an honest null into a fabricated fact. It also catches a value captured at the fold and
#          dropped before PromptResult - the structurally-dead shape #475 shipped once already.
#
#          The --filter names this pair's OWN test class, never a plan-wide trait - a trait-only filter
#          asserts the state of every test in the plan, so this task could not go green until a task that
#          DEPENDS on it had run (a deadlock validate and graph --check cannot see, #455). This plan
#          introduces no trait at all, so this is shape 3: the class term alone.
#          'ModelDigestCaptureTests' was checked against all 195 existing Core test class names and every
#          other class this plan authors - it is a substring of none (notably NOT of
#          ModelDigestProvenanceTests, task 09's class), so the filter is discriminating.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179): default `dotnet test` prints them mid-run and ends with only [FAIL] <name>.
#
# NO -v q on the TEST command (#179/#462): it suppresses the entire Error Message / Expected / Actual /
#          Stack Trace block, leaving only "[FAIL] <name>" for the re-emit below to find.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:'), which would invert the
# zero-match guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this guardrail runs this pair's tests and cannot run without it."
    exit 1
}

$filter = 'FullyQualifiedName~ModelDigestCaptureTests'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# EXIT CODE FIRST on a forward (assert-pass) check (#455): a test host that never ran exits NON-zero
# with no summary at all, so checking the exit code first reports its real error instead of blaming the
# filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "ModelDigestCaptureTests is red. Fix src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs - do NOT edit the test file, which is outside this task's writeScope and would fail the write-scope check. If the WholeCompletion case is the one failing, the fingerprint was lifted in ApplyChunk only. If AResponseWithNoSystemFingerprint_LeavesTheDigestNull is the failure, an absent fingerprint is being filled with '' or with the model tag: absent must stay absent."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count
# [Skip]ped tests, so a fully-skipped class would clear a Total-keyed guard.
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. The class ModelDigestCaptureTests was not found, or the filter is malformed - this guardrail is certifying nothing. This is NOT a finding about the implementation."
    exit 1
}

Write-Output "ModelDigestCaptureTests green: $executed tests executed, 0 failed."
exit 0
