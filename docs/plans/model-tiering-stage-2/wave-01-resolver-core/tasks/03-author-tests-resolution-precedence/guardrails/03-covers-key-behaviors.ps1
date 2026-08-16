# catches: a precedence test file that asserts only the easy half - "a pin wins" - and never pins
#          down the DoR's own explicit correction, that action.effort ALONE is NOT a bypass. That is
#          the one precedence rule an implementer is most likely to get backwards (effort mirrors
#          model's SHAPE but not its BYPASS), and the legacy-fallback case is what Invariant 7 rests
#          on.
#
#          Tokens are matched over COMMENT-STRIPPED code (#97/#98): the raw-content form is satisfied
#          by a checklist comment naming every token above a single trivially-failing [Fact]
#          (measured - it exited 0 on that sample). The test-count floor closes the other half.
# Scoped to the ONE file this task owns (grep-scope rule).
$path = "tests/Guardrails.Core.Tests/ModelTiering/TierResolverPrecedenceTests.cs"
if (-not (Test-Path $path)) {
    Write-Output "$path does not exist - the test file was not authored"
    exit 1
}
$raw = Get-Content -Raw -Path $path

$noVerb = [regex]::Replace($raw, '@"(?:[^"]|"")*"', '""')
$noStr  = [regex]::Replace($noVerb, '"(\\.|[^"\\])*"', '""')
$code   = [regex]::Replace($noStr, '/\*[\s\S]*?\*/', '')
$code   = [regex]::Replace($code, '(?m)//.*$', '')

$facts = [regex]::Matches($code, '\[\s*(Fact|Theory)\b').Count
if ($facts -lt 4) {
    Write-Output "$path declares only $facts [Fact]/[Theory] test(s) - DoR 6.1 enumerates five precedence behaviors (runner pin, model pin, effort-is-not-a-bypass, defaultTier, legacy fallback) plus the Invariant 7 fixture. One or two tests cannot encode them"
    exit 1
}

$missing = @()
if ($code -notmatch '[Rr]unner')                        { $missing += "action.runner full pin" }
if ($code -notmatch '[Mm]odel')                         { $missing += "action.model full pin" }
if ($code -notmatch '[Ee]ffort')                        { $missing += "action.effort is NOT a bypass" }
if ($code -notmatch '[Dd]efaultTier')                   { $missing += "tiering.defaultTier fallback" }
if ($code -notmatch '[Ll]egacy|[Ff]allback')            { $missing += "the legacy fallback path" }
if ($code -notmatch '[Tt]ierSource')                    { $missing += "the tierSource provenance (task | plan-default | override, DoR 12.4)" }

if ($missing.Count -gt 0) {
    Write-Output ("$path does not encode: " + ($missing -join "; ") + " - DoR 6.1 requires all of these; a file asserting only that a pin wins leaves the effort-is-not-a-bypass correction and Invariant 7's legacy path unproven")
    exit 1
}
exit 0
