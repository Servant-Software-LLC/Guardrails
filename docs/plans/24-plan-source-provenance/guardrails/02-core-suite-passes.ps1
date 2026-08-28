# catches: a green-per-task plan that broke something it never looked at - every task's own filtered
#          tests passed, and the union regressed a neighbouring behaviour in Guardrails.Core that no
#          task-level --filter selected. This plan edits InitialBreakdownInvoker and the state/ contract,
#          both of which have existing coverage outside the PlanSourceProvenance trait, so the whole-area
#          suite is the only check that can see that. It is also the second half of the #181 baseline:
#          green START before the DAG, green END on the merged HEAD.
#
# scope: LOCAL (no sidecar `scope` key), deliberately. A whole-suite run is a TERMINAL POSTCONDITION.
# Tagging it scope:"integration" would re-run it at every union point, on partial merges where a
# downstream TDD task has not run yet - so the deliberately-red author-tests of one half would red-halt
# a correct run. That is the #125 anti-pattern by name.
#
# NO --filter here, and that is not an oversight: the #455 rule governs TASK-LEVEL filters, whose job is
# to name the pair's own class. This is the terminal whole-suite gate, the one place "all tests pass"
# belongs, so there is nothing to scope it to.
#          Re-emits the assertion/exception lines at the END so a red terminal gate's WHY reaches the
#          operator, not just `[FAIL] <name>` (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the Guardrails.Core test suite is RED on the merged plan-branch HEAD - the union of this plan's tasks regressed something (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): a test host that started, executed nothing and exited 0 would otherwise
# certify the whole suite green over an empty run. Key on the EXECUTED count (Passed+Failed; "Total:"
# would also count [Skip]ped tests), never on a verbosity-dependent string (#248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this terminal gate certified nothing. tests/Guardrails.Core.Tests carries hundreds of tests, so zero executed means the project path is wrong or the test host never started, not that the suite is empty."
    exit 1
}
exit 0
