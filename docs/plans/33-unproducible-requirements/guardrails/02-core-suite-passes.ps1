# catches: a merged HEAD whose Core suite is red - including the CORPUS SWEEP, which lives in this suite
#          as ProducerCoverageCorpusTests and is the gate that proves GR2060 neither fires on a correct
#          plan nor goes mute. The sweep's expectation is per plan and per commit and carries a required
#          NON-ZERO; section 11 prohibition 5 forbids re-baselining it to a tolerance or a blanket zero.
#          Because this is a TERMINAL gate, a red here WITHHOLDS DELIVERY rather than merging.
#
# LOCAL, not scope integration (#165): a whole-suite run is a terminal postcondition. At an intermediate
#          union a downstream task has not run yet - task 3's tests are red until task 4 lands - so an
#          integration-scoped whole-suite check would red-halt a correct run. It belongs here, once.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$log = & dotnet test 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj' --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -ne 0) {
    Write-Output ''
    Write-Output '=== Core suite failures on the merged HEAD (detail re-emitted) ==='
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') { Write-Output $line }
    }
    Write-Output ''
    Write-Output 'The Core suite is red on the merged plan-branch HEAD, so delivery is withheld. If the failure is in ProducerCoverageCorpusTests, read it as a RESULT before reading it as a bug: a finding on a plan the expectation table says should be silent is what this sweep exists to surface. Baseline for comparison: 2260 passing, 0 failing on master @67859c7 before this plan.'
    exit 1
}

$passed = 0; $failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed
if ($executed -lt 1) {
    Write-Output 'NO TESTS EXECUTED on the merged HEAD. A test host that never ran must not be certified as a green terminal gate.'
    exit 1
}

Write-Output ("Core suite green on the merged HEAD: " + $executed + " tests executed, 0 failed (2260 was the pre-plan baseline).")
exit 0
