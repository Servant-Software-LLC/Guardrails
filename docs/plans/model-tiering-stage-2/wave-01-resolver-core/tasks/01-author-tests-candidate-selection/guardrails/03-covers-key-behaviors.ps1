# catches: a test file that compiles and fails but only exercises ONE slice of DoR 6.2 - typically
#          just "ascending strength" - leaving the costly floor, the climb and no-route unasserted.
#          Those are the rules with no override anywhere in the design, so a red-but-shallow test file
#          would let the implementation task go green having violated the floor on the climb.
#
#          The tokens are matched over COMMENT-STRIPPED code (#97/#98). Without that this check is
#          satisfied by a `// DoR 6.2 checklist: ServesTier / strength / costly / climb / no-route`
#          block sitting above ONE trivially-failing [Fact] - measured: the naive raw-content form
#          exited 0 on exactly that sample, which would leave the whole 6.2 contract asserted by
#          nothing. A test-count floor closes the other half of that exploit.
# Scoped to the ONE file this task owns (grep-scope rule).
$path = "tests/Guardrails.Core.Tests/ModelTiering/TierResolverCandidateSelectionTests.cs"
if (-not (Test-Path $path)) {
    Write-Output "$path does not exist - the test file was not authored"
    exit 1
}
$raw = Get-Content -Raw -Path $path

# Strip string literals and comments BEFORE matching, so neither a checklist comment nor a message
# string can satisfy a coverage token. Verbatim strings (@"...") are stripped first.
$noVerb = [regex]::Replace($raw, '@"(?:[^"]|"")*"', '""')
$noStr  = [regex]::Replace($noVerb, '"(\\.|[^"\\])*"', '""')
$code   = [regex]::Replace($noStr, '/\*[\s\S]*?\*/', '')
$code   = [regex]::Replace($code, '(?m)//.*$', '')

# Structural: real xUnit tests must be present, and ENOUGH of them. Six behaviors are enumerated in
# the action prompt; one [Fact] cannot encode six. The floor is deliberately below six (a [Theory]
# legitimately covers several) but above the single-stub exploit.
$facts = [regex]::Matches($code, '\[\s*(Fact|Theory)\b').Count
if ($facts -lt 4) {
    Write-Output "$path declares only $facts [Fact]/[Theory] test(s) - DoR 6.2 enumerates six behaviors (candidacy, ascending strength, unspecified-last, the costly floor at BOTH rungs, the climb, no-route). One or two tests cannot encode them; a coverage grep passing on a comment is exactly what this floor exists to stop"
    exit 1
}

# NOTE: deliberately no `ServesTier` token. The prompt tells the author to drive behavior THROUGH
# the resolver, so a correct, complete test file need never name the predicate - measured: a correct
# 6-[Fact] file covering every enumerated behavior was false-RED by that token, and "fixing" it meant
# adding a semantically empty reference to Stage 1 code. Candidacy is already covered by the costly
# and climb tokens below, and that the resolver CONSUMES ServesTier is task 02's structural guardrail.
$missing = @()
if ($code -notmatch '[Ss]trength')               { $missing += "ascending-strength ordering" }
if ($code -notmatch '[Cc]ostly')                 { $missing += "the costly floor" }
if ($code -notmatch '[Cc]limb|[Ss]tronger')      { $missing += "the climb to a stronger rung" }
if ($code -notmatch 'no.?route|NoRoute')         { $missing += "the no-route condition" }
if ($code -notmatch '[Cc]ostlyCeiling|[Ee]xcludedOnlyBecauseCostly|[Cc]eiling') {
    $missing += "the D28 binding-ceiling datum (the resolution must REPORT that a stronger block was excluded only because it is costly)"
}

if ($missing.Count -gt 0) {
    Write-Output ("$path does not encode: " + ($missing -join "; ") + " - DoR 6.2 (and D28) require all of these; a test file asserting only strength ordering lets the implementation violate the costly floor on a climb and gives wave 2 nothing to report the ceiling from")
    exit 1
}
exit 0
