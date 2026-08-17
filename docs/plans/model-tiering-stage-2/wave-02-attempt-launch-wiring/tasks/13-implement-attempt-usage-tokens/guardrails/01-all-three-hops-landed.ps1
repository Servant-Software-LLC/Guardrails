# catches: an implementation that lands the hop the authored tests CAN see and skips the one they
#          cannot. AttemptUsageTokensTests reaches the PARSER and the ClaudeResult -> PromptResult
#          mapping; nothing in Core.Tests observes AttemptJournaler writing AttemptRecord.Usage. So
#          a correct parser wired to a journaler that never records it passes every test this pair
#          owns, and the datum dies one hop short of run.json - where task 11's aggregator is the
#          only consumer. Same shape as task 11's CLI-suppression gap, and an acknowledged
#          second-best: the strong form is a journal round-trip test, which task 01 owns.
#
# SOUND ABSENCE ONLY (#468). Each probe below is an ABSENCE check whose failure is conclusive: if a
# file never names the symbol, the hop cannot have happened. Presence proves nothing on its own -
# CORRECTNESS is the sibling 02-usage-tokens-tests-pass guardrail's job, not this one's. No probe
# here asserts the SHAPE of an implementation.
$ErrorActionPreference = 'Continue'
$failures = @()

function Read-Code([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $t = Get-Content -Raw $path
    $t = [regex]::Replace($t, '/\*[\s\S]*?\*/', '')
    return [regex]::Replace($t, '(?m)//.*$', '')     # comment-blind probes are the #97/#98 defect
}

# --- HOP 1: the parser must read BOTH cache fields, or the input total is wrong by construction ---
$parser = Read-Code 'src/Guardrails.Core/Prompts/ClaudeStreamParser.cs'
if ($null -eq $parser) {
    $failures += 'src/Guardrails.Core/Prompts/ClaudeStreamParser.cs does not exist'
}
else {
    foreach ($f in @('cache_creation_input_tokens', 'cache_read_input_tokens')) {
        if ($parser -cnotmatch [regex]::Escape($f)) {
            $failures += "[hop 1] ClaudeStreamParser never reads '$f' in real code - InputTokens is the TOTAL input consumed (input_tokens + cache_creation_input_tokens + cache_read_input_tokens). On this plan's own wave-1 output input_tokens was 3,706 against an actual 4,627,863, so omitting a cache field understates volume by ~1250x. This is an ABSENCE check: it cannot tell you the sum is right, only that it cannot be"
        }
    }
}

# --- HOP 2: the runner must carry it onto PromptResult ---------------------------------------
$runner = Read-Code 'src/Guardrails.Core/Prompts/ClaudePromptRunner.cs'
if ($null -eq $runner) {
    $failures += 'src/Guardrails.Core/Prompts/ClaudePromptRunner.cs does not exist'
}
elseif ($runner -cnotmatch 'Usage') {
    $failures += '[hop 2] ClaudePromptRunner never mentions Usage in real code - the parsed usage is not carried onto PromptResult, so it stops at the parser. Add it beside the existing `CostUsd = result.CostUsd` mapping'
}

# --- HOP 3: the journaler must record it - the hop NO authored test can observe ----------------
$journaler = Read-Code 'src/Guardrails.Core/Execution/AttemptJournaler.cs'
if ($null -eq $journaler) {
    $failures += 'src/Guardrails.Core/Execution/AttemptJournaler.cs does not exist'
}
elseif ($journaler -cnotmatch 'Usage') {
    $failures += '[hop 3] AttemptJournaler never mentions Usage in real code - AttemptRecord.Usage stays null on every attempt, so the schema member task 01/02 landed is never populated and task 11''s per-tier line reports cost only. This is the hop the unit tests CANNOT see: they stop at PromptResult'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== usage-token wiring: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "the usage datum must travel parser -> PromptResult -> AttemptRecord. A gap at any hop leaves #230-lite's token axis empty, and a costless local provider then reports no evidence of what it did at all."
    exit 1
}
exit 0
