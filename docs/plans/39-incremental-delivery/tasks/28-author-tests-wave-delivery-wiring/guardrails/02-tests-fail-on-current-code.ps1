# catches: a hollow test passing off as TDD red. dotnet test exits non-zero if ANY selected test
#          fails, so an Assert.True(true) body hides behind its genuinely-failing siblings and the
#          suite-level exit code certifies nothing about it. This binds every enumerated behaviour to a
#          PINNED test method name and requires each to be observed Failed in the runner's own TRX —
#          never stdout, never --list-tests, both of which a hollow body satisfies as well as a real one
#          (#375).
#
#          The red comes from CURRENT CODE, not from a throwing stub. This task's only production edit is
#          RunReport.WaveDeliveries, a WORKING property that defaults to empty: hundreds of tests construct
#          and print RunReport, so a throwing getter would break them. Each pinned row is red because the
#          Scheduler on this base writes no waves.<dir>.delivered record, raises no WaveDelivered, and
#          stamps no WaveDeliveries (the #120 shape task 29 closes).
#
#          BOUNDARY: this proves each test is COUPLED TO THE CODE PATH, not that its assertion is
#          correct. An invoking-then-hollow test is red on this base, green after, and PASSES this.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$pinned = @(
    'TheDeliveryIsJournaledRunning_BeforeTheUsersBranchMoves',
    'ADeliveredWave_IsRecordedDeliveredWithThePromotedCommit',
    'ADeliveryCovers_EveryWaveSinceTheLastDelivery',
    'CoversAfterAResume_StillStartsAfterTheLastDeliveredWave',
    'TheSchedulerRaisesWaveDelivered_AfterTheRecordIsPersisted',
    'ARefusedDelivery_IsRecordedRefusedWithItsOutcome_AndRaisesNoEvent',
    'ASuppressedDelivery_IsRecordedSuppressed_AndNeverMovesTheUsersBranch',
    'AHaltedRunsReport_StillCarriesEarlierWaveDeliveries',
    'ATrialThatCannotBeBuilt_IsRecordedRefused_AndIsNeverPromoted'
)

# DECLARED RED-CENSUS EXEMPTION (review 2026-09-13, B5) — APlanMarkingNoWave_RecordsNoDeliveryAndReportsNone.
#   STRUCTURAL REASON: green on this base by construction. It is the never-weaker requirement: a plan none
#   of whose waves sets `delivers: true` gets no `delivered` key, an empty RunReport.WaveDeliveries, and no
#   WaveDelivered. On this base NOTHING writes the record, fills the map or raises the event for ANY plan,
#   and the stub property defaults to empty, so a correct test passes before task 29 lands. Coupling it to
#   the missing wiring to force a red would make it wrong once the wiring exists.
#   It is asserted to EXIST below, and task 29's forward census requires it to be observed Passed — that is
#   where a wiring that records or raises for a non-delivering wave turns it red.
$mustExist = @('APlanMarkingNoWave_RecordsNoDeliveryAndReportsNone')

$results = Join-Path $env:TEMP ("gr39-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    # Class-scoped, never the bare plan-wide trait (#455).
    & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
        --filter "FullyQualifiedName~WaveDeliveryWiringTests" `
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
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~WaveDeliveryWiringTests' matched NO tests. A zero-match filter exits 0 and certifies nothing."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Failed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Failed'. A behaviour that passes before the Scheduler is wired is not TDD red — it is hollow, or it asserts something this base already does."
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
