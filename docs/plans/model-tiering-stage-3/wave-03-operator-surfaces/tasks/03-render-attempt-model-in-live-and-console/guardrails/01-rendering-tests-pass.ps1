# catches: a renderer that announces the attempt's model but throws away the mismatch - the surface that
#          "always prints one string", which the brief names as the failure that discards the entire
#          reason #349 exists. The four tests this selects pin the formatter's two-string and one-string
#          forms as DISTINGUISHABLE, pin the plain surface's line as CONTAINING the shared formatter's own
#          output for the same inputs (an agreement property, so an inlined copy that is identical today
#          fails the moment either side drifts), and pin that LiveRunObserver DECLARES the member rather
#          than silently inheriting IRunObserver's empty default body.
#          Re-emits the failure DETAIL at the END so the WHY reaches the retry tail (#179, dotnet.md 4.2).
#
# FILTER SCOPE (#455): named on THIS task pair's OWN test class, never the plan-wide trait. The sibling
# classes AttemptModelDisclosureTests and AttemptModelForwardingTests are deliberately EXCLUDED - tasks
# 02 and 04 make those green and run in PARALLEL with this one, so selecting them would deadlock this
# task behind siblings it does not depend on. `~AttemptModelRenderingTests` is discriminating: it is a
# substring of no other test class in either test project (checked against AttemptModelDisclosureTests,
# AttemptModelForwardingTests, ActionModelResolutionTests, ActionModelOverrideTests,
# ObservedModelCaptureTests, PromptExecutionSupportModelTests).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the zero-match guard reads is LOCALIZED (#455)

$filter = 'FullyQualifiedName~AttemptModelRenderingTests'
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
    Write-Output "AttemptModelRenderingTests is not green. The deliverables are (a) LiveRunObserver.AttemptModelSummary implemented so the two-string and one-string forms differ, (b) ConsoleRunObserver.AttemptModelResolved writing a line that CONTAINS that formatter's output verbatim, and (c) LiveRunObserver.AttemptModelResolved declared and rendering under the gate. A NotImplementedException here means the stub is still in place."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing, or a
# malformed one, also exits 0. Key on the EXECUTED count (Passed+Failed); "Total:" would also count
# [Skip]ped tests, so a fully-skipped class would pass it. Never on "No test matches ..." (that string
# is verbosity-dependent and never appears here, the #248 failure).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests or is malformed; AttemptModelRenderingTests is authored by 01-author-tests-attempt-model-surfaces and must be present in tests/Guardrails.Integration.Tests."
    exit 1
}
exit 0
