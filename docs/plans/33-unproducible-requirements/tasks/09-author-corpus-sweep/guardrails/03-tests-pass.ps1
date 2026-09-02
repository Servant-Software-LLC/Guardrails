# catches: a corpus sweep that does not actually pass - most often because a plan folder produces a finding the expectation table does not carry.
#
# FORWARD polarity, so the ordering is exit-code check FIRST (a test host that never ran must not be
# misreported as a bad filter), then the zero-match guard. The guard is keyed on the EXECUTED count
# (Passed + Failed), never on 'Total:' - which counts [Skip]ped tests, so a fully-skipped class would
# clear it - and never on the "no tests matched" STRING, which is verbosity-dependent (#248).
$ErrorActionPreference = 'Continue'
# The summary line is LOCALIZED (a German-culture box prints 'gesamt:' and no 'Total:'), which would
# invert the guard into an unconditional failure. Pin it before the run, not after (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
$filter  = 'FullyQualifiedName~ProducerCoverageCorpusTests'

# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the re-emit
# below exists to surface, defeating #179 by the flag alone.
$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -ne 0) {
    Write-Output ''
    Write-Output '=== ProducerCoverageCorpusTests failures (detail re-emitted) ==='
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') { Write-Output $line }
    }
    Write-Output ''
    Write-Output 'The corpus sweep fails. A finding on a plan the table expects to be silent is a RESULT, not a licence to re-baseline: section 11 prohibition 5 forbids flattening the expectation to a tolerance or a blanket zero. Escalate with needsHuman naming the plan and the finding.'
    exit 1
}

$passed = 0; $failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed
if ($executed -lt 1) {
    Write-Output 'FILTER MATCHED NOTHING: 0 tests executed for ProducerCoverageCorpusTests. A filter that selects nothing exits 0 and certifies nothing - the exact zero-match hole #455 exists to close. Fix the filter or the test class name.'
    exit 1
}

Write-Output ("ProducerCoverageCorpusTests green: " + $executed + " tests executed, 0 failed.")
exit 0
