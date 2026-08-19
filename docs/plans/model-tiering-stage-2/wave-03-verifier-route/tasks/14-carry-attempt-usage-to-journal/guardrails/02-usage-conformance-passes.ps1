# catches: a #475 fix that declares the members and still does not deliver a number. The sibling 01
#          guardrail is structural and says so - it proves Usage appears wherever CostUsd appears, not
#          that a token count survives the trip to run.json.
#
# This clause is the ONLY thing in the wave that reads the value back out. It asserts STRICTLY
# POSITIVE counts on BOTH record paths: `anyUsage` stays false for an all-zero aggregate, so a
# non-null or zero-token assertion would pass against the very bug being fixed (the #73 hollow-output
# shape), and a serial-only assertion would certify the half-fix that drops the value in worktree
# mode - the default.
#
# NAMED CLAUSE, not the class (#455): the rest of Stage2ConformanceTests belongs to tasks 07, 08 and
# 12 and is gated by them.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$test = 'Attempt_UsageTokensReachRunJson_BothPaths'
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
    Write-Output "=== attempt usage does not reach run.json (exit $testExit) ==="
    Write-Output "This clause reads run.json AFTER a real run through the Stage2 harness. Declaring the member is not the deliverable - the value ARRIVING on BOTH record paths is. If only the serial path carries it, PendingAttempt is the hop you skipped."
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
