# catches: a pinned wiring behaviour that was never written, or no longer runs, once the Scheduler is
#          wired. Task 28's red census excuses five rows from being Failed (each is green on its base by
#          construction) but NOT from existing; this is the other half of that bargain — all nineteen of
#          task 28's behaviours observed Passed in the runner's own TRX. It is also where the exempt rows
#          turn red: a wiring that records or raises for a non-delivering wave, settles a fresh record (or
#          raises again) over a delivery that was already recorded, or writes a record before task 08's
#          delivery predicate (a serial run, mergeOnSuccess off, or a #556 definition divergence).
#          01-tests-pass accepts a SKIPPED test; this does not.
#
#          FORWARD polarity, and its boundary stated: a forward census cannot see a hollow body
#          (a hollow test passes). What it CAN see is a test that was never written, or one that
#          no longer runs.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$pinned = @(
    'TheDeliveryIsJournaledRunning_BeforeTheTrialIsBuilt',
    'ADeliveredWave_IsRecordedDeliveredWithThePromotedCommit',
    'ADeliveryCovers_EveryWaveSinceTheLastDelivery',
    'CoversAfterAResume_StillStartsAfterTheLastDeliveredWave',
    'TheSchedulerRaisesWaveDelivered_AfterTheRecordIsPersisted',
    'ARefusedDelivery_IsRecordedRefusedWithItsOutcome_AndRaisesNoEvent',
    'ASuppressedDelivery_IsRecordedSuppressed_AndNeverMovesTheUsersBranch',
    'AHaltedRunsReport_StillCarriesEarlierWaveDeliveries',
    'ATrialThatCannotBeBuilt_IsRecordedRefused_AndIsNeverPromoted',
    'AResumeAfterACrashMidDelivery_RecordsAnAlreadyDeliveredTrialAsDelivered',
    'AForcedDelivery_NamesTheDecisionItOverrodeInItsDetail',
    'AFailedTrialTreeGate_IsRecordedRefused_AndIsNeverPromoted',
    'ALaterBarrierAfterAHookRejection_IsRecordedSuppressedNamingIt',
    'AResumeAfterAHookRejection_StillHoldsLaterDeliveries',
    'APlanMarkingNoWave_RecordsNoDeliveryAndReportsNone',
    'AResumeOverADeliveredRecord_KeepsItAndRaisesNoEvent',
    'ASerialWavedRun_NeverDeliversAtABarrier',
    'ABarrierDelivery_WithMergeOnSuccessOff_WritesNoRecord',
    'ADivergedTaskDefinition_BlocksTheBarrierDelivery'
)

$results = Join-Path $env:TEMP ("gr39-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    # Class-scoped, never the bare plan-wide trait (#455).
    & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
        --filter "FullyQualifiedName~WaveDeliveryWiringTests" `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION, not an unbound-behaviour report: "the run did not happen" is a different
        # fact from "the tests did not behave as required".
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). Fix that first; this is NOT a statement about the pinned behaviours."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($nodes.Count -lt 1) {
        # The zero-match hole (#455/#248): with nothing executed the TRX carries no <Results>
        # element, so the dotted navigation yields $null and @($null).Count is 1 — an unfiltered
        # .Count check would evaluate 1 -lt 1 and never fire. Hence the Where-Object above.
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~WaveDeliveryWiringTests' matched NO tests. A zero-match filter exits 0 and certifies nothing — fix the filter or the class name."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Passed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Passed'."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Census: all $($pinned.Count) pinned behaviour(s) observed Passed."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}
