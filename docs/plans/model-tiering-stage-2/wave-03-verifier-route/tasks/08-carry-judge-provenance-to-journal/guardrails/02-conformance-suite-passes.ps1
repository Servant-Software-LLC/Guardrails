# catches: a carry that lands the datum but breaks something on the way. This task threads a field
#          through TaskExecutor - a file every task of every plan runs through - so the blast radius
#          of a mistake here is the whole harness, not this wave. Wave 2's nine facts exercise the
#          same execution path and are the REGRESSION BAR; they run here for that reason.
#          Re-emits the assertion lines at the END so they reach the retry-feedback tail (#179).
#
# THE FILTER AND THE FLOOR ARE ONE INVARIANT - keep them in step. An earlier revision narrowed this
# filter to six named judge clauses (a #455 scoping fix) and left the floor at 14, which was derived
# from the WHOLE class (wave 2's nine + wave 3's five). Six clauses can never sum to 14, so the
# guardrail was arithmetically unsatisfiable and dead-ended the task on every possible attempt. The
# narrowing had also quietly dropped wave 2's nine clauses - the regression bar this gate exists for.
#
# So: run the whole class MINUS only the clauses a LATER task is responsible for, and set the floor to
# exactly what that leaves. 17 tests in the class today, 2 excluded, floor 15. If you add a clause to
# Stage2ConformanceTests, this floor moves with it.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Excluded because they are RED until a task that runs AFTER this one lands - including them would
# fail this task on work it is forbidden to do:
#   Judge_WeakVerifier_AdvisoryRecorded_EqualAndStrongNot -> task 12 records the advisory
#   Attempt_UsageTokensReachRunJson_BothPaths             -> task 14 carries #475's usage
$notYet = @(
    'Judge_WeakVerifier_AdvisoryRecorded_EqualAndStrongNot',
    'Attempt_UsageTokensReachRunJson_BothPaths'
)
$expected = 15
$filter = 'FullyQualifiedName~Stage2ConformanceTests' +
          (($notYet | ForEach-Object { "&FullyQualifiedName!~$_" }) -join '')

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
    Write-Output "=== the Stage 2 conformance suite is not green (exit $testExit) ==="
    Write-Output "If a WAVE-2 clause is failing, this task regressed a shared execution path rather than threading a field through it. If Judge_ProvenanceReachesRunJson_BothPaths is failing, the judge is not reaching run.json on one of the two record paths - check the worktree/settle path, not just the serial one."
    Write-Output ""
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion lines matched - inspect the full log above)" }
    exit 1
}

# ZERO-MATCH GUARD (#455), keyed on the EXECUTED count (Passed+Failed); Total: counts [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt $expected) {
    Write-Output "exit 0 but only $ran conformance test(s) executed, expected at least $expected - this gate certified less than it should. The class has 17 tests and this filter excludes $($notYet.Count); a lower count means a clause was renamed, dropped, or [Skip]ped. If you ADDED a clause, raise `$expected to match."
    exit 1
}
exit 0
