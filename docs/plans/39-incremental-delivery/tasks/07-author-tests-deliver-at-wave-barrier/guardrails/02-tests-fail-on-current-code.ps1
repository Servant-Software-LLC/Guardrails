# catches: a hollow test passing off as TDD red. dotnet test exits non-zero if ANY selected test
#          fails, so an Assert.True(true) body hides behind its genuinely-failing siblings and the
#          suite-level exit code certifies nothing about it. This binds every enumerated behaviour to a
#          PINNED test method name and requires each to be observed Failed in the runner's own TRX —
#          never stdout, never --list-tests, both of which a hollow body satisfies as well as a real one
#          (#375).
#
#          BOUNDARY: this proves each test is COUPLED TO THE CODE PATH, not that its assertion is
#          correct. An invoking-then-hollow test is red on stubs, green after, and PASSES this.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$pinned = @(
    'ADeliveringWaveMergesAtItsOwnBarrier',
    'ANonDeliveringWaveRidesAlongToTheNextDeliveryPoint',
    'TheGateRunsAgainstTheMergedTree_NotThePlanBranchAlone',
    'TheOperatorOverride_LiftsABarrierSuppression'
)

# DECLARED RED-CENSUS EXEMPTIONS (reviews 2026-09-11 and 2026-09-13). Each row is TRUE on this task's
# base, because nothing delivers at a wave barrier yet, so a correct test is green on arrival. Pinning
# any of them red would force a test coupled to the missing feature, which task 08 cannot then turn
# green without editing tests.
#   APlanMarkingNoWave_StillMergesOnceAtRunEnd
#     STRUCTURAL REASON: the never-weaker requirement. A plan marking no wave already merges once at
#     run end on this base (the #340 default), and a correct implementation must leave that unchanged.
#   AWaveWhoseExitGateFails_DoesNotDeliver
#     STRUCTURAL REASON: the base has no per-wave delivery, so a wave whose exit gate fails already
#     delivers nothing.
#   AFailedExitGateAfterTheTrialMerge_LeavesTheUsersBranchUnmoved
#     STRUCTURAL REASON: the base never writes the user's branch at a wave barrier, so it is unmoved
#     whatever the gate says.
#   AFailedTrialGate_LeavesThePlanBranchUnmoved
#     STRUCTURAL REASON: the base never merges the user's branch into the plan branch, so the user's
#     mid-run commit is never an ancestor of it.
#   TheTrialRefIsDeleted_AfterEitherOutcome
#     STRUCTURAL REASON: the base never creates refs/guardrails/trial/<waveDir>, so the ref is absent
#     after either outcome.
#   ABarrierDelivery_WithMergeOnSuccessOff_NeverPromotes
#     STRUCTURAL REASON: the base never delivers at a wave barrier, and with mergeOnSuccess off Finalize
#     delivers nothing at run end, so the user's branch is unmoved.
#   ADeliversWaveWithNoExitGate_DoesNotDeliverAtItsBarrier
#     STRUCTURAL REASON: the base never delivers at a wave barrier, and the run halts at the later wave's
#     exit gate, a path that returns before Finalize, so nothing is delivered at run end either.
#   ADivergedTaskDefinition_BlocksTheBarrierDelivery
#     STRUCTURAL REASON: the base never delivers at a wave barrier, and a run with a recorded #556
#     executed-definition divergence is never AllSucceeded, so Finalize delivers nothing at run end.
#   Each is asserted to EXIST below, and task 08's forward census requires each to be observed Passed.
#   That is where a merge-before-gate implementation, a leaked trial ref, an ignored opt-out, a
#   delivery behind an empty gate or a delivery past a divergence turns them red.
$mustExist = @(
    'APlanMarkingNoWave_StillMergesOnceAtRunEnd',
    'AWaveWhoseExitGateFails_DoesNotDeliver',
    'AFailedExitGateAfterTheTrialMerge_LeavesTheUsersBranchUnmoved',
    'AFailedTrialGate_LeavesThePlanBranchUnmoved',
    'TheTrialRefIsDeleted_AfterEitherOutcome',
    'ABarrierDelivery_WithMergeOnSuccessOff_NeverPromotes',
    'ADeliversWaveWithNoExitGate_DoesNotDeliverAtItsBarrier',
    'ADivergedTaskDefinition_BlocksTheBarrierDelivery'
)

$results = Join-Path $env:TEMP ("gr39-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
        --filter "FullyQualifiedName~WaveBarrierDeliveryTests" `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). This is NOT a statement about the pinned behaviours."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($nodes.Count -lt 1) {
        # @($null).Count is 1, so the Where-Object filter above is what lets this guard fire at all.
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~WaveBarrierDeliveryTests' matched NO tests. A zero-match filter exits 0 and certifies nothing."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Failed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Failed'. A behaviour that passes against the stubs is not TDD red — it is hollow or already implemented."
        }
    }

    # The DECLARED exemptions are exempt from the RED requirement, not from EXISTING. A test that
    # is never written is not "green because correct" — it is absent, and absence is how a
    # never-weaker guarantee quietly stops being asserted anywhere.
    foreach ($name in $mustExist) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX. It is DECLARED-EXEMPT from the red census (a correct implementation leaves it green), NOT exempt from existing. Write it."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Red census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Red census: all $($pinned.Count) pinned behaviour(s) observed Failed."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}
