# catches: a #501 regression test that is not actually red - either because it asserts today's behaviour
#          (so task 6's fix would BREAK it), or because it is hollow. Unlike task 3's red, this one must
#          COMPILE AND RUN: ProducerCoverage exists by now, so the tests must fail on an ASSERTION,
#          which is what makes task 6's mitigation observable. A compile failure here would be a
#          different bug wearing the red's clothes.
#
# POLARITY: INVERSE (TDD red), so no #179 re-emit. The guard runs FIRST here - unlike a forward check -
#          because a crash or a zero-match must not be certified as TDD red (#455 ordering rule).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$file = 'tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs'
if (-not (Test-Path -LiteralPath $file)) {
    Write-Output ('PRECONDITION: ' + $file + ' does not exist. This task authors it.')
    exit 1
}

$log = & dotnet test 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj' --nologo --filter 'FullyQualifiedName~JitPrefixVetoTests' 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

$passed = 0; $failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

# Guard FIRST on the inverse polarity: a build break or a filter typo also exits non-zero, and either
# would certify a test host that never ran as a TDD red.
if ($executed -lt 1) {
    Write-Output ''
    Write-Output 'NO TESTS EXECUTED. The expected red here is an ASSERTION failure - the tests must compile and RUN and disagree with today behaviour. Zero executed means the project did not build, or the filter matches nothing. Neither is TDD red; fix the build or the class name.'
    exit 1
}

if ($code -eq 0) {
    Write-Output ''
    Write-Output 'THE REGRESSION TESTS ALREADY PASS, so they are not pinning the #501 defect. Today GR2060 is an ERROR that is NOT in Scheduler.UnsatisfiableWhileIncomplete, so a JIT partial prefix tripping it IS reverted - a test that passes now is asserting the wrong thing, and task 6 would break it. Assert the behaviour you WANT: the prefix survives, and the finding still appears in the gate-decision report.'
    exit 1
}

Write-Output ("TDD red confirmed: " + $executed + " tests executed, " + $failed + " failed on assertions. Task 6 makes them green.")
exit 0
