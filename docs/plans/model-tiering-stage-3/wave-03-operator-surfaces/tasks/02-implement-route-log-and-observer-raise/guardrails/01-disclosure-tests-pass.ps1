# catches: a route-log change that renders the model the harness ASKED for rather than the one that
#          RAN - the single failure #349 exists to prevent - and an event declared but never raised.
#          The five tests this selects drive the REAL TaskExecutor attempt loop through the shipped
#          Stage2PlanHarness and read attempt-route.log back OFF DISK, so a value computed and never
#          written cannot satisfy them (the #475 shape: AttemptRecord.Usage shipped declared, read, and
#          assigned by no construction site, with every guardrail green).
#          Re-emits the failure DETAIL at the END so the WHY reaches the retry tail (#179, dotnet.md 4.2).
#
# FILTER SCOPE (#455): named on THIS task pair's OWN test class, never the plan-wide trait. The two
# sibling classes AttemptModelRenderingTests and AttemptModelForwardingTests are deliberately EXCLUDED -
# they are made green by tasks 03 and 04, which run in PARALLEL with this one, so selecting them would
# deadlock this task behind a sibling it does not depend on. `~AttemptModelDisclosureTests` is
# discriminating: it is a substring of no other test class in either test project (checked against
# ActionModelResolutionTests, ActionModelOverrideTests, ObservedModelCaptureTests,
# PromptExecutionSupportModelTests, AttemptModelRenderingTests, AttemptModelForwardingTests).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the zero-match guard reads is LOCALIZED (#455)

$filter = 'FullyQualifiedName~AttemptModelDisclosureTests'
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
    Write-Output "AttemptModelDisclosureTests is not green. The two deliverables are (a) attempt-route.log re-written AFTER the '#349: fold the OBSERVED model' block, carrying a 'requested model:' line ONLY when provenance.RequestedModel is non-null, and (b) _observer.AttemptModelResolved raised from the attempt loop with provenance.Model and provenance.RequestedModel passed verbatim."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing, or a
# malformed one, also exits 0. Key on the EXECUTED count (Passed+Failed); "Total:" would also count
# [Skip]ped tests, so a fully-skipped class would pass it. Never on "No test matches ..." (that string
# is verbosity-dependent and never appears here, the #248 failure).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests or is malformed; AttemptModelDisclosureTests is authored by 01-author-tests-attempt-model-surfaces and must be present in tests/Guardrails.Integration.Tests."
    exit 1
}
exit 0
