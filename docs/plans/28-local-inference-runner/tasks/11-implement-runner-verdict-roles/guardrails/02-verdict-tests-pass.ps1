# catches: a runner that SYNTHESISES a verdict rather than transcribing one, serves an Action invocation it cannot honestly honour, or reports ServesRoles by reading the same field it reads rather than by construction.
#
# SCOPE (#455): filtered to THIS task pair's OWN test class, never a plan-wide trait. A trait-keyed
#          filter would assert the state of every test this plan authors - failing forward (this task
#          cannot go green until a DOWNSTREAM task runs) or inverse (a sibling's red satisfies it).
#          Discriminating: 'OpenAiCompatVerdictTests' matches no other test class in this plan or the target project.
$ErrorActionPreference = 'Continue'

# The dotnet summary line is LOCALIZED; pin the culture BEFORE the run or the executed-count guard
# below inverts into an unconditional failure on a non-English box (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
$filter = 'FullyQualifiedName~OpenAiCompatVerdictTests'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# FORWARD polarity: check the exit code FIRST, so a test host that never ran is not misreported as a
# bad filter. Then the zero-match guard.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Failure detail (re-emitted at the END so the WHY reaches the retry feedback, #179) ==="
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match '^\s*(\[FAIL\]|Error Message:|Expected:|Actual:|\s+at\s)') { Write-Output $line }
    }
    Write-Output ""
    Write-Output "OpenAiCompatVerdictTests is not green. Fix the implementation - do NOT edit the tests (they are outside this task's writeScope and an edit fails the write-scope check)."
    exit 1
}

# Zero-match guard: keyed on the EXECUTED count (Passed + Failed), never 'Total:' (which counts
# [Skip]ped tests) and never the "no tests matched" string (verbosity-dependent, so it never fires).
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. An exit code of 0 over an empty set certifies nothing - the class was renamed, or it never landed."
    exit 1
}

Write-Output "$cls green: $executed executed, 0 failed."
exit 0
