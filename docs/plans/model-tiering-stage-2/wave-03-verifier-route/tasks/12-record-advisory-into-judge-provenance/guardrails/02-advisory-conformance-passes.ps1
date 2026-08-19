# catches: an advisory that is computed, assigned, and never actually arrives in run.json. The sibling
#          01 guardrail is structural and says so - it proves the CODE NAMES the right things, not that
#          the value survives the trip. This is the behavioural half, and without it this task would be
#          the #475 shape it exists to prevent: a datum with a plausible-looking assignment and no
#          proof of arrival.
#
# NAMED CLAUSE, not the class (#455). Task 06 authored Judge_WeakVerifier_AdvisoryRecorded_EqualAndStrongNot
# specifically for this task; the rest of Stage2ConformanceTests belongs to tasks 07 and 08 and is
# already gated by them. The clause asserts BOTH polarities - a weak judge records `advisory`, an
# equal-and-strong one records none - because a test that only checks "an advisory appeared" cannot
# tell a working rule from one that flags everything.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$test = 'Judge_WeakVerifier_AdvisoryRecorded_EqualAndStrongNot'
$filter = "FullyQualifiedName~Stage2ConformanceTests.$test"

$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== the advisory does not arrive (exit $testExit) ==="
    Write-Output "This clause reads run.json AFTER a real run through the Stage2 harness. Naming VerifierAdvisory and assigning Advisory is not the deliverable - the value ARRIVING is."
    Write-Output ""
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 with nothing executed certifies nothing.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the filter matched nothing. Either $test was never authored (task 06 pins that exact name), it was renamed, or it is [Skip]ped."
    exit 1
}
exit 0
