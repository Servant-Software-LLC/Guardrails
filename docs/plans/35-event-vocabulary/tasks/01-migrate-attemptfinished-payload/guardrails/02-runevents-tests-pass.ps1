# catches: a migration that COMPILES but changed the emitted rows. RunEventStream and
#          ObserverProjection must write byte-identical rows after this task - widening them is tasks
#          05 and 07. The existing RunEvents tests assert the current bytes, so they are the
#          behaviour-preservation proof. Scoped to Category=RunEvents rather than the whole suite (the
#          terminal gate's job) so a retry costs seconds, not eight minutes.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$failures = New-Object System.Collections.Generic.List[string]

# Count FLOORS (#521). All 41 Core RunEvents tests live in three files THIS TASK OWNS, and they are its
# only behaviour-preservation proof - so the task can satisfy its own guardrail by deleting or disabling
# the assertion that catches its regression. "Do not change what any existing test asserts" is prose
# against a 17-file, 75-turn task under retry pressure; these numbers are not.
# Measured on 4e4785e: Core 41, Integration 32. A floor catches deletion and disabling.
# It does NOT catch an assertion RELAXED inside a still-passing test - a named, accepted residual.
$floors = @{
    'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'               = 41
    'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj' = 32
}

foreach ($proj in @(
    'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj',
    'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj')) {

    $log  = & dotnet test $proj --filter "Category=RunEvents" --nologo 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Output "===== $proj ====="
    Write-Output $log

    # Zero-match guard (#455): key on the EXECUTED count, never Total (which counts [Skip]ped tests).
    $passed = 0; $failed = 0
    if ($log -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
    if ($log -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
    if (($passed + $failed) -lt 1) {
        $failures.Add("[$proj] the Category=RunEvents filter executed ZERO tests - it certifies nothing. The trait or the project moved.")
        continue
    }
    if ($passed -lt $floors[$proj]) {
        $failures.Add("[$proj] only $passed RunEvents test(s) passed; this plan was authored against $($floors[$proj]). A test was deleted, disabled or renamed out of the category - a suite with fewer tests still goes green, so this floor is the only thing that sees it.")
    }
    if ($code -ne 0) {
        $failures.Add("[$proj] $failed of $($passed + $failed) RunEvents test(s) failed - the migration changed emitted behaviour.")
        $log -split "`r?`n" | Where-Object {
            $_ -match '^\s*(Error Message|Expected|Actual|Stack Trace|\s+at )' -or $_ -match '\[FAIL\]'
        } | ForEach-Object { $failures.Add("    $_") }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== RunEvents behaviour changed by the migration ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "RunEvents tests pass in both projects - the migration preserved behaviour."
exit 0
