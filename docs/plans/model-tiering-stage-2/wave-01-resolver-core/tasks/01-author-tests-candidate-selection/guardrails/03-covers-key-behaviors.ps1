# catches: a test file that compiles and fails but only exercises ONE slice of DoR 6.2 - typically
#          just "ascending strength" - leaving the costly floor, the climb, no-route and the D22a
#          agreement property unasserted. Those are the rules with no override anywhere in the design.
#
# This is a LOWER BOUND, not a proof (catalogue #75). It cannot tell an assertion from a mention; it
# only makes a shallow file cost a retry. Three measured evasions shaped the checks below:
#   - a checklist COMMENT above one stub [Fact] satisfied a raw-content scan  -> comment-stripped
#   - ONE [Theory]/[Fact] "covering" six behaviours                           -> declaration-count floor
#   - one unused line, `var unused = new { r.Costly, r.Climbed, r.NoRoute };` -> distinct-line rule
#     (every token is real code, so stripping does nothing; the fix is that a behaviour genuinely
#      under test appears in its test's NAME or on more than one line)
$path = "tests/Guardrails.Core.Tests/ModelTiering/TierResolverCandidateSelectionTests.cs"
if (-not (Test-Path $path)) {
    Write-Output "$path does not exist - the test file was not authored"
    exit 1
}
$raw = Get-Content -Raw -Path $path

$noRaw  = [regex]::Replace($raw, '"""[\s\S]*?"""', '""')
$noVerb = [regex]::Replace($noRaw, '@"(?:[^"]|"")*"', '""')
$noStr  = [regex]::Replace($noVerb, '"(\\.|[^"\\])*"', '""')
$code   = [regex]::Replace($noStr, '/\*[\s\S]*?\*/', '')
$code   = [regex]::Replace($code, '(?m)//.*$', '')
$lines  = $code -split "`r?`n"

$facts = [regex]::Matches($code, '\[\s*(Fact|Theory)\b').Count
if ($facts -lt 5) {
    Write-Output "$path declares only $facts [Fact]/[Theory] test(s) - DoR 6.2 enumerates six behaviors plus the D22a agreement property. Note this counts DECLARATIONS, not executed tests: one [Theory] with six [InlineData] rows reports six executed and proves one behavior."
    exit 1
}

# The trait is what EVERY downstream filter selects on (Category=TierResolution&...). Without it the
# pair's tests-pass / tests-fail-on-stubs guardrails match nothing and report a confusing zero-match.
# Matched against $raw: the attribute's value is a string literal, which the stripper removes.
$traits = [regex]::Matches($raw, '\[\s*Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]').Count
if ($traits -lt 1) {
    Write-Output "$path declares no [Trait(`"Category`", `"TierResolution`")] - every guardrail in this plan filters on Category=TierResolution, so untagged tests are invisible to all of them and the pair's checks will report a zero match. Tag the class or every test."
    exit 1
}

# A behaviour is CREDITED only when its token is (a) part of a TEST METHOD NAME, or (b) on a line that
# also ASSERTS. Two weaker rules were measured and rejected: "appears anywhere" fell to a checklist
# comment, and "on >=2 distinct lines OR anywhere on a method-declaration line" fell to a single
# one-line test whose body was `var unused = new { r.Costly, r.Climbed, ... };` - the declaration and
# the vacuous object share a line, so the name branch matched. Requiring an Assert on the same line
# encodes the actual intent: the behaviour is EXERCISED, not name-dropped.
function Test-Behavior([string[]]$patterns, [string]$label) {
    foreach ($p in $patterns) {
        if ($code -match ('(?:void|Task)\s+\w*(?:' + $p + ')\w*\s*\(')) { return $true }
        if (@($lines | Where-Object { $_ -match $p -and $_ -match 'Assert' }).Count -ge 1) { return $true }
    }
    return $false
}

$missing = @()
if (-not (Test-Behavior @('[Ss]trength') 'strength'))                            { $missing += "ascending-strength ordering (weakest capable model wins)" }
if (-not (Test-Behavior @('[Cc]ostly') 'costly'))                                { $missing += "the costly floor" }
if (-not (Test-Behavior @('[Cc]limb', '[Ss]tronger') 'climb'))                   { $missing += "the climb to a stronger rung" }
if (-not (Test-Behavior @('no.?route', 'NoRoute') 'no-route'))                   { $missing += "the no-route condition" }
if (-not (Test-Behavior @('ServesTier') 'agreement'))                            { $missing += "the D22a agreement property (the candidate set must agree with PromptRunnerConfig.ServesTier for every block/rung pair - this is the test that replaces the deleted structural greps)" }
if (-not (Test-Behavior @('[Cc]eiling', 'ExcludedOnly', 'OnlyBecauseCostly', 'CostBound', 'BoundByCost', 'CostlyRefused', '[Bb]lockedByCost') 'ceiling')) {
    $missing += "the D28 binding-ceiling datum - the resolution must REPORT that a stronger block was excluded ONLY because it is costly. Accepted spellings include: CostlyCeiling, ExcludedOnly..., OnlyBecauseCostly, CostBound, BoundByCost, BlockedByCost... (this is a NAMING requirement, not a missing assertion - if you already assert the behaviour, rename the member rather than adding tests)"
}

if ($missing.Count -gt 0) {
    Write-Output ("$path does not encode: " + ($missing -join "; ") + " -- each behaviour must appear in a test METHOD NAME or on at least two distinct lines; a single mention is a name-drop, not coverage")
    exit 1
}
exit 0
