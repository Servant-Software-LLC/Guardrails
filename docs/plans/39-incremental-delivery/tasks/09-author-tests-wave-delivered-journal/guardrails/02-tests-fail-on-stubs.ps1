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
    'Delivered_RoundTripsThroughTheJournalJson',
    'Delivered_CarriesAtCommitAndCovers',
    'EveryStatusToken_RoundTrips',
    'RecordWaveDelivery_ReplacesTheRecordAndPersists',
    # A-N1 (review of 1a809bce): JournalJson.Options writes nulls, so a running record without
    # [JsonIgnore(WhenWritingNull)] on At/Commit/Outcome/Detail carries four null keys. Red on this base
    # because the stub's getters throw when the serializer reads them.
    'ARunningRecord_WritesNoSettledKeys',
    # Lead follow-up to the review of 1a809bce: the trial-tree gate's refusal outcome. Red on this base
    # because JournalJson.DeliveryOutcomeToken's discard arm throws on the unregistered member.
    'TheTrialGateFailedOutcome_RoundTrips',
    # Review round 5, d39-rewind-delivered-wave: a rewind keeps the delivered record. Red on this base
    # because ResetWaveToPending replaces the whole entry with a bare pending one (RunJournal.cs,
    # `UpdateWave(waveDir, new WaveJournalEntry { Status = WaveStatus.Pending })`); RecordWaveDelivery
    # also throws here. After task 10 only a preserving reset turns it green.
    'ResettingADeliveredWave_KeepsItsDeliveryRecord'
)

# DECLARED RED-CENSUS EXEMPTION (review 2026-09-13, B5 restructure) — AWaveEntryWithoutADelivery_OmitsTheKey.
#   STRUCTURAL REASON: green on the stub by construction. The stub's `Delivered` property on
#   WaveJournalEntry is a WORKING nullable container carrying [JsonIgnore(WhenWritingNull)], the same
#   attribute as the Entry/Exit markers beside it (src/Guardrails.Core/Journal/JournalModel.cs), so an
#   entry that never set it serializes with no "delivered" key before WaveDeliveredRecord is implemented.
#   Pinning it red would force a test coupled to the stubbed record, which task 10 cannot then turn
#   green without editing tests.
#   It is asserted to EXIST below, and task 10's forward census requires it to be observed Passed.
#   (Covers_NamesTheNonDeliveringWavesThatRodeAlong and TheDeliveryIsJournaledRunning_BeforeTheMerge
#   moved to task 28, which drives the real Scheduler: this task's files cannot produce either.)
$mustExist = @('AWaveEntryWithoutADelivery_OmitsTheKey')

$results = Join-Path $env:TEMP ("gr39-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
        --filter "FullyQualifiedName~WaveDeliveredJournalTests" `
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
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~WaveDeliveredJournalTests' matched NO tests. A zero-match filter exits 0 and certifies nothing."
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
