# catches: a carry that lands the datum but breaks something on the way. This task edits Scheduler.cs
#          and TaskExecutor.cs - the files every task of every plan runs through - so the blast radius
#          of a mistake here is the whole harness, not this wave. The WHOLE conformance class runs
#          here (not just the five judge clauses): wave 2's nine facts exercise the same execution
#          path this task threads a field through, and they are the regression bar.
#          Re-emits the assertion lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'FullyQualifiedName~Stage2ConformanceTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual block (#462).
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455).
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output ""
    Write-Output "the conformance suite is not green after threading judge provenance through the execution path. If a WAVE-2 clause is the failing one, this task regressed the shared path rather than adding to it - add your field beside the ones already there and do not restructure a method around it."
    exit 1
}

# ZERO-MATCH GUARD (#455), keyed on the clause count the class owes after this wave.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 14) {
    Write-Output "exit 0 but only $ran conformance test(s) executed - wave 2 owes nine and wave 3 adds five, so 14 is the floor. A lower count means a test was renamed, dropped or [Skip]ped; the plan terminal gate matches the same names and will fail there too."
    exit 1
}
exit 0
