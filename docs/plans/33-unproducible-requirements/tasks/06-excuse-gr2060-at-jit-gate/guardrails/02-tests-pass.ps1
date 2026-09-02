# catches: a mitigation that makes the #501 regression tests pass the wrong way - most likely by making the GR2060 finding VANISH rather than merely stop casting a veto, which would pass test 1 and be a worse bug than the one it fixed.
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
$filter  = 'FullyQualifiedName~JitPrefixVetoTests'

# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the re-emit
# below exists to surface, defeating #179 by the flag alone.
$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -ne 0) {
    Write-Output ''
    Write-Output '=== JitPrefixVetoTests failures (detail re-emitted) ==='
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') { Write-Output $line }
    }
    Write-Output ''
    Write-Output 'The #501 regression tests still fail. Check the three properties the fix must preserve: excused errors stay in the REPORT, the suppression is scoped to the JIT breakdown gate and not to validate, and PlanIsClosed stays as the empty-stub-wave suppressor. Keying the excuse on PlanIsClosed instead of wavePrefixIsIncomplete is the trap - it returns true for a partial prefix.'
    exit 1
}

$passed = 0; $failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed
if ($executed -lt 1) {
    Write-Output 'FILTER MATCHED NOTHING: 0 tests executed for JitPrefixVetoTests. A filter that selects nothing exits 0 and certifies nothing - the exact zero-match hole #455 exists to close. Fix the filter or the test class name.'
    exit 1
}

Write-Output ("JitPrefixVetoTests green: " + $executed + " tests executed, 0 failed.")
exit 0
