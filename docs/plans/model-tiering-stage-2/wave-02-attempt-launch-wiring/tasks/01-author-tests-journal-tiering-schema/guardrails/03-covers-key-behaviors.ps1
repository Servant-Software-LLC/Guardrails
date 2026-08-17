# catches: a red-but-THIN suite - one test that throws on the no-route token, nothing else. That
#          clears both siblings (it compiles; it exits non-zero) while leaving the tierSource
#          mapping, the round-trip and the ABSENT-NOT-NULL half unencoded - and absent-not-null is
#          the whole backward-compatibility guarantee for every existing user's run.json.
#          Structural, scoped to the ONE file this task owns.
$ErrorActionPreference = 'Continue'
$file = 'tests/Guardrails.Core.Tests/ModelTiering/JournalTieringSchemaTests.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test file this task owes was never written"
    exit 1
}
$content = Get-Content -Raw $file

# The class-level trait: without it the plan-root baseline preflight (Category!=TierResolution) would
# sweep this plan's own intentionally-red tests into a later baseline, and every downstream filter
# in this wave that pairs the trait with the class name would match nothing.
if ($content -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') {
    $failures += 'the class carries no [Trait("Category", "TierResolution")] - required so the plan-root baseline preflight excludes it and this wave''s filters select it'
}

# A real xUnit fact/theory must exist - a file of helper methods and comments would satisfy a bare
# keyword grep (dotnet.md 17.1).
if ($content -notmatch '\[(Fact|Theory)\]') {
    $failures += 'no [Fact]/[Theory] attribute - the file declares no executable test'
}

# The 12.4 tokens, each required VERBATIM (they are wire format, so the spelling IS the contract).
foreach ($token in @('"no-route"', '"plan-default"', '"override"', '"task"')) {
    if ($content -notmatch [regex]::Escape($token)) {
        $failures += "the wire token $token is never asserted - 12.4 pins these exact spellings, and the kebab form of plan-default is precisely what a plain Enum.ToString would get wrong"
    }
}

# The five new provenance members + the usage block must each be exercised by name.
foreach ($member in @('TierSource', 'Runner', 'Kind', 'Effort', 'Usage', 'InputTokens', 'OutputTokens')) {
    if ($content -notmatch "\b$member\b") {
        $failures += "the provenance/usage member '$member' is never referenced - the 12.4 delta is not fully encoded"
    }
}

# The ABSENT-NOT-NULL half. It can ONLY be proven against the emitted TEXT: an absent key and a
# `null` key deserialize identically, so a test that round-trips through the reader and asserts null
# passes on a journal that writes `"tier": null` for every existing user. Require a text-level
# negative assertion.
if ($content -notmatch '(DoesNotContain|NotContains)') {
    $failures += 'no negative/text-level assertion (Assert.DoesNotContain or equivalent) - the absent-not-null requirement cannot be proven by re-reading the object, because an absent key and a null key deserialize identically. Assert on the emitted JSON TEXT'
}

# The backward-compatibility read: an OLD journal (provenance with only today''s keys, or none).
if ($content -notmatch '(?i)(older|legacy|backward|pre-?exist|without\s+provenance|no\s+provenance)') {
    $failures += 'nothing exercises reading an OLDER journal (an attempt whose provenance carries only today''s #198/#382 keys, and one with no provenance at all) - behaviour 7 of the task prompt'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== JournalTieringSchemaTests coverage: $($failures.Count) gap(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "the authored suite is red but THIN - it clears the build and the fail-on-stubs check while leaving part of the DoR 12.4 delta unencoded. Encode the missing behaviours listed above."
    exit 1
}
exit 0
