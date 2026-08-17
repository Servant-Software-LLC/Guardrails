# catches: a precedence test file that asserts only the easy half - "a pin wins" - and never pins
#          down the DoR's own explicit correction, that action.effort ALONE is NOT a bypass, nor
#          Invariant 7's harder fixture.
#
# A LOWER BOUND, not a proof (catalogue #75). Three measured evasions shaped it: a checklist COMMENT
# (-> comment-stripped); one [Theory] with six rows (-> DECLARATION count); and a single unused line
# `var unused = new { r.Model, r.Effort, ... }` where every token is real code (-> distinct-line rule).
# Two tokens were also measured VACUOUS and are gone: bare `[Rr]unner` and `[Mm]odel` are satisfied by
# the ambient type names `PromptRunnerConfig` / `ModelTier` and by the file's own
# `namespace Guardrails.Core.Tests.ModelTiering;` line.
$path = "tests/Guardrails.Core.Tests/ModelTiering/TierResolverPrecedenceTests.cs"
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
    Write-Output "$path declares only $facts [Fact]/[Theory] test(s) - DoR 6.1 enumerates the runner pin, the model pin, effort-is-not-a-bypass, the defaultTier fallback and the legacy path, plus Invariant 7's two fixtures. This counts DECLARATIONS, not executed tests."
    exit 1
}

$traits = [regex]::Matches($raw, '\[\s*Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]').Count
if ($traits -lt 1) {
    Write-Output "$path declares no [Trait(`"Category`", `"TierResolution`")] - every guardrail in this plan filters on Category=TierResolution, so untagged tests are invisible to all of them. Tag the class or every test."
    exit 1
}

# A behaviour is CREDITED only when its token is (a) part of a TEST METHOD NAME, or (b) on a line that
# also ASSERTS. Two weaker rules were measured and rejected: "appears anywhere" fell to a checklist
# comment, and "on >=2 distinct lines OR anywhere on a method-declaration line" fell to a single
# one-line test whose body was `var unused = new { r.Costly, r.Climbed, ... };` - the declaration and
# the vacuous object share a line, so the name branch matched. Requiring an Assert on the same line
# encodes the actual intent: the behaviour is EXERCISED, not name-dropped.
function Test-Behavior([string[]]$patterns) {
    foreach ($p in $patterns) {
        if ($code -match ('(?:void|Task)\s+\w*(?:' + $p + ')\w*\s*\(')) { return $true }
        if (@($lines | Where-Object { $_ -match $p -and $_ -match 'Assert' }).Count -ge 1) { return $true }
    }
    return $false
}

$missing = @()
# Discriminating forms only - the bare type names are ambient in this file.
if (-not (Test-Behavior @('\.Runner\s*=', 'Runner\s*=\s*"', '[Pp]in')))          { $missing += "the action.runner full pin (use a discriminating form - the bare word 'Runner' is satisfied by the ambient type PromptRunnerConfig)" }
if (-not (Test-Behavior @('\.Model\s*=', 'Model\s*=\s*"', '[Mm]odelPin')))       { $missing += "the action.model full pin (the bare word 'Model' is satisfied by ModelTier and by the namespace line)" }
if (-not (Test-Behavior @('[Ee]ffort')))                                          { $missing += "action.effort is NOT a bypass - the DoR's own explicit correction, and the precedence rule most likely to be built backwards" }
if (-not (Test-Behavior @('[Dd]efaultTier')))                                     { $missing += "the tiering.defaultTier fallback" }
if (-not (Test-Behavior @('[Ll]egacy', '[Ff]allback')))                           { $missing += "the legacy fallback path" }
if (-not (Test-Behavior @('[Rr]outingEnabled', '[Zz]eroTag', '[Uu]ntagged', '[Ii]nvariant7', '[Ii]nvariant_7'))) {
    $missing += "Invariant 7 fixture (b) - routing blocks PRESENT, no tier on the action, no defaultTier => still the LEGACY path with zero tier-resolution activity. Fixture (a) (no routing anywhere) is the easy half and cannot catch the case an implementer gets wrong"
}

if ($missing.Count -gt 0) {
    Write-Output ("$path does not encode: " + ($missing -join "; ") + " -- each behaviour must appear in a test METHOD NAME or on at least two distinct lines; a single mention is a name-drop, not coverage")
    exit 1
}
exit 0
