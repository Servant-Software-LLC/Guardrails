# catches: a wave-2 branch whose OWN unit suite regressed once merged with its siblings. The four
#          Core.Tests classes this wave authors are made green in four INDEPENDENT segments that
#          never see each other - the journal-schema pair (01/02), the unavailability pair (03/04),
#          the per-tier-spend pair (10/11) and the usage-tokens pair (12/13). Three of them write to
#          overlapping type surfaces (AttemptProvenance / AttemptRecord / PromptResult), so this is
#          the first tree on which all four run together.
#
#          Distinct from the sibling 02 gate, which proves the INTEGRATION seam, and from
#          01-wave-union-builds, which proves only that the union COMPILES - a compiling union can
#          still have a merged AttemptJournaler that journals usage but no longer journals no-route.
#
# LOCAL - no scope key (#165): a wave terminal postcondition. At an intermediate union inside this
# wave, the classes whose implementing task has not run yet are legitimately RED.
# Re-emits the failure DETAIL at the END so the WHY reaches the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)

# Named alternation over the four classes THIS WAVE owns - never the bare plan-wide trait, which
# would also select wave 1's classes (already proven by wave 1's own exit gate) and every class a
# later wave adds (dotnet.md 4.3, shape 2: parenthesise, bare '|', no backslash).
$classes = @(
    'JournalTieringSchemaTests',
    'ConnectionUnavailabilityClassificationTests',
    'PerTierSpendTests',
    'AttemptUsageTokensTests'
)
$filter = 'Category=TierResolution&(' + (($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|') + ')'

$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
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
    Write-Output "one of this wave's four Core.Tests suites is red on the merged wave HEAD - a sibling branch regressed it (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): keyed on the EXECUTED count (Passed+Failed); "Total:" counts [Skip]ped.
# Four classes must each contribute at least one executed test.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 4) {
    Write-Output "exit 0 but only $ran test(s) executed across the four wave-2 Core.Tests classes - this gate certified nothing. A class was renamed, dropped, lost its [Trait(`"Category`", `"TierResolution`")], or every matched test is [Skip]ped. Expected classes: $($classes -join ', ')."
    exit 1
}
exit 0
