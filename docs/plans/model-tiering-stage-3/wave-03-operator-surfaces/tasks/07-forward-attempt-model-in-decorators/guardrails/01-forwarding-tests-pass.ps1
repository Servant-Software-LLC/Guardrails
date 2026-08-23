# catches: a decorator that silently DROPS the new default-method event - the exact shape the brief calls
#          out ("the interface compiles, the live table renders it because the test exercised the inner
#          observer directly, and the on-the-fly log site and diagram quietly never see it"). The three
#          tests this selects construct each decorator around a recording inner observer and assert the
#          call ARRIVED - which no compiler check, no build, and no whole-suite run can do, because an
#          omitted default-method member is legal C# that resolves to an empty body.
#          The third test reflects over the whole Guardrails.Cli assembly, so a THIRD decorator added
#          after this wave is caught by the same clause rather than needing a new one.
#          Re-emits the failure DETAIL at the END so the WHY reaches the retry tail (#179, dotnet.md 4.2).
#
# FILTER SCOPE (#455): named on THIS task pair's OWN test class, never the plan-wide trait. The sibling
# classes AttemptModelDisclosureTests and AttemptModelRenderingTests are deliberately EXCLUDED - tasks 02
# and 03 make those green and run in PARALLEL with this one, so selecting them would deadlock this task
# behind siblings it does not depend on. `~AttemptModelForwardingTests` is discriminating: it is a
# substring of no other test class in either test project (checked against AttemptModelDisclosureTests,
# AttemptModelRenderingTests, ActionModelResolutionTests, ActionModelOverrideTests,
# ObservedModelCaptureTests, PromptExecutionSupportModelTests).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the zero-match guard reads is LOCALIZED (#455)

$filter = 'FullyQualifiedName~AttemptModelForwardingTests'
# NO -v q on a TEST command: it suppresses the Error Message / Expected / Actual / Stack Trace block,
# leaving the re-emit below nothing but test NAMES and defeating #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first

# EXIT CODE FIRST, guard second (#455, FORWARD polarity): a test host that never ran exits NON-zero with
# no summary at all, so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "AttemptModelForwardingTests is not green. Both OnTheFlyLogSiteObserver and OnTheFlyDiagramObserver must DECLARE 'public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)' and forward all four arguments to _inner - add it beside the existing _inner.VerifierAdvisoryFound line in each. If the reflection test named a THIRD decorator type, that type is outside this task's writeScope: escalate rather than widening the edit."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing, or a
# malformed one, also exits 0. Key on the EXECUTED count (Passed+Failed); "Total:" would also count
# [Skip]ped tests, so a fully-skipped class would pass it. Never on "No test matches ..." (that string
# is verbosity-dependent and never appears here, the #248 failure).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests or is malformed; AttemptModelForwardingTests is authored by 01-author-tests-attempt-model-surfaces and must be present in tests/Guardrails.Integration.Tests."
    exit 1
}
exit 0
