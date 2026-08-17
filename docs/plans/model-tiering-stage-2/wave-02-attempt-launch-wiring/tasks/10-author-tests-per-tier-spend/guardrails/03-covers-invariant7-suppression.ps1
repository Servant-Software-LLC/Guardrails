# catches: a suite that encodes the AGGREGATION (the easy, satisfying half) and skips the
#          SUPPRESSION rule. That is not a coverage nicety: 9.3's rule is stricter than "add a
#          per-tier section" - on a tiering-INACTIVE run the summary must print exactly today's cost
#          line, with no per-tier section and no `untiered:` bucket. An aggregator that emits an
#          empty or `untiered:` section on every existing user's run is the single most likely way
#          this wave breaks Invariant 7, and no assertion about sums will ever notice.
#          Structural, scoped to the ONE test file this task owns.
$ErrorActionPreference = 'Continue'
$file = 'tests/Guardrails.Core.Tests/ModelTiering/PerTierSpendTests.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test file this task owes was never written"
    exit 1
}
$content = Get-Content -Raw $file

if ($content -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') {
    $failures += 'the class carries no [Trait("Category", "TierResolution")] - required so the plan-root baseline preflight excludes it and this wave''s filters select it'
}
if ($content -notmatch '\[(Fact|Theory)\]') {
    $failures += 'no [Fact]/[Theory] attribute - the file declares no executable test'
}

# The suppression half, in the ONLY form that can prove it.
if ($content -notmatch '(?i)untiered') {
    $failures += 'the string "untiered" never appears - 9.3 names that bucket explicitly as the thing that must NOT be emitted, so the suite needs a NEGATIVE assertion that it is absent from the rendered text. A test that never mentions it cannot rule it out'
}
if ($content -notmatch '(DoesNotContain|NotContains)') {
    $failures += 'no negative/text-level assertion (Assert.DoesNotContain or equivalent) - the suppression rule is about what is NOT printed, which cannot be proven by inspecting a collection of rung buckets: an empty section and no section are the same object and different output'
}
if ($content -notmatch '(?i)(Null|Empty)') {
    $failures += 'nothing asserts the zero-tiered-attempt case returns null/empty - that is the return value task 11 wires the CLI''s `if (... is { } summary)` suppression on'
}

# The aggregation half, and the degradation 9.3 calls out by name.
foreach ($needle in @(
    @{ Pattern = '(?i)inputTokens|outputTokens'; What = 'token aggregation - the surface that lets a costless local provider still show volume, which is why #230-lite has a tokens axis at all' },
    @{ Pattern = '(?i)overhead';                 What = 'the overhead-spend case - JournalCost folds document.OverheadCostUsd into the total, and that spend belongs to no rung' })) {
    if ($content -notmatch $needle.Pattern) {
        $failures += "nothing exercises $($needle.What)"
    }
}

# All three rungs must appear, or the ascending-order behaviour is untested.
foreach ($rung in @('easy', 'medium', 'hard')) {
    if ($content -notmatch "`"$rung`"") {
        $failures += "the rung `"$rung`" never appears - the stable ascending order (ActionTiers.All) cannot be asserted without all three"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== PerTierSpendTests coverage: $($failures.Count) gap(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "the suite is red but does not yet pin the Invariant 7 suppression rule, which is the half that protects every existing single-model user's run summary."
    exit 1
}
exit 0
