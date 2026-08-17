# catches: a suite that encodes the EASY half - "usage parses", "absent means null" - and skips the
#          one case this task exists for. On real runner output `usage.input_tokens` is 3,706 while
#          the attempt consumed 4,627,863 input tokens; the difference is entirely in
#          cache_creation_input_tokens + cache_read_input_tokens. A suite that never mentions those
#          two fields cannot distinguish a correct implementation from one understating volume by
#          ~1250x - and #230-lite is explicitly "the evidence base for whether the deferred
#          subsystems are ever worth building", so a silently-wrong number is worse than none.
#          Structural, scoped to the ONE test file this task owns. A LOWER BOUND, not a proof
#          (catalogue #75): it cannot tell an assertion from a mention, only make a shallow file
#          cost a retry.
#
# TOKEN CHOICE: the two cache field names are the probe precisely because they are UNAMBIGUOUS -
# they appear nowhere else in this codebase and cannot be satisfied by an ambient type name. The
# bare word "Usage" is deliberately NOT used: it is in this file's own class name
# (AttemptUsageTokensTests) and would be satisfied by the namespace line alone (#468 taxonomy, the
# vacuous-token failure).
$ErrorActionPreference = 'Continue'
$file = 'tests/Guardrails.Core.Tests/ModelTiering/AttemptUsageTokensTests.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test file this task owes was never written"
    exit 1
}
$raw = Get-Content -Raw $file

# Comment-stripped for the behavioural probes (#97/#98): a checklist comment naming every field is
# not coverage. The Trait probe below reads $raw deliberately - its value is a string literal, which
# the stripper removes.
$code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')

if ($raw -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') {
    $failures += 'the class carries no [Trait("Category", "TierResolution")] - required so the plan-root baseline preflight excludes it and this wave''s exit gate selects it'
}
$facts = [regex]::Matches($code, '\[\s*(Fact|Theory)\b').Count
if ($facts -lt 4) {
    $failures += "the file declares only $facts [Fact]/[Theory] test(s) - six behaviours are owed (full input total, partial fields, absent-is-null, last-result-wins, malformed-does-not-throw, PromptResult carries it). This counts DECLARATIONS, not executed tests: one [Theory] with six [InlineData] rows reports six executed and can prove one behaviour"
}

# THE case. Both cache fields, or the total is not being tested.
foreach ($f in @('cache_creation_input_tokens', 'cache_read_input_tokens')) {
    if ($code -cnotmatch [regex]::Escape($f)) {
        $failures += "the field '$f' never appears in real code - InputTokens is the TOTAL input consumed (input_tokens + cache_creation_input_tokens + cache_read_input_tokens). A suite that omits this field cannot tell a correct implementation from one that reads input_tokens alone and understates volume by ~1250x on real output"
    }
}

# Absent-is-null, the discipline that keeps a costless provider's real volume from reading as 0 tok.
if ($code -notmatch 'Assert\s*\.\s*Null') {
    $failures += 'no Assert.Null - DoR 12.4 is absent-not-null: a result event with no usage object must yield null, NOT AttemptUsage { 0, 0 }, because a zeroed record CLAIMS nothing was consumed and task 11''s aggregator degrades on null'
}

# Resilience: the parser is fed untrusted runner output on every attempt.
if ($code -notmatch '(?i)malformed|invalid|non.?numeric|notanobject|badusage|garbage') {
    $failures += 'nothing exercises a MALFORMED usage payload - a usage that is a string/number, or whose token fields are non-numeric, must leave Usage null and must NOT break parsing of total_cost_usd or num_turns on the same line. A throw here fails an otherwise-successful task'
}

# The parser's existing last-wins contract must extend to the new member.
if ($code -notmatch '(?i)last.?result|second.?result|two.?result|last.?wins|overrides') {
    $failures += 'nothing exercises the LAST-RESULT-WINS rule - ClaudeStreamParser''s documented contract is that the last result message wins, and the new Usage member must follow it rather than keeping the first'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== AttemptUsageTokensTests coverage: $($failures.Count) gap(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "the suite is red but does not yet pin the cache-inclusive input total, which is the half that decides whether #230-lite's token axis reports reality or a number ~1250x too small."
    exit 1
}
exit 0
