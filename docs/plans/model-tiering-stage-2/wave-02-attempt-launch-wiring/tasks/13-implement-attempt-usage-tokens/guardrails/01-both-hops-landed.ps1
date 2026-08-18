# catches: an implementation that lands the PARSE hop and drops the CARRY hop - the parser can be
#          perfectly correct while ClaudePromptRunner never copies the value onto PromptResult, and
#          the parser-only half of the authored suite would still be green.
#
# WAS `01-all-three-hops-landed`. The third hop (journalling onto AttemptRecord) is GONE from this
# task, and that is a CORRECTION rather than a relaxation: it was UNREACHABLE from this task's
# writeScope. AttemptJournaler builds its AttemptRecord from an ActionRun, and ActionRun carries
# CostUsd with no Usage sibling (ActionRunner.cs:352) - so no edit to AttemptJournaler.cs could
# populate it, and the only in-scope way to satisfy the old clause was a token that journals nothing,
# i.e. a false green. A previous attempt found exactly this and honestly halted (#474). Landing that
# hop needs ActionRunner.cs, RunReport.cs and Scheduler.cs, none of which this task owns.
#
# SOUND ABSENCE ONLY (#468). Each probe is an ABSENCE check whose failure is conclusive: if a file
# never names the symbol, the hop cannot have happened. Presence proves nothing on its own -
# CORRECTNESS is the sibling 02-usage-tokens-tests-pass guardrail's job, not this one's.
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

# --- HOP 2: the runner must carry it onto PromptResult -----------------------------------------
$runner = Read-Code 'src/Guardrails.Core/Prompts/ClaudePromptRunner.cs'
if ($null -eq $runner) {
    $failures += 'src/Guardrails.Core/Prompts/ClaudePromptRunner.cs does not exist'
}
elseif ($runner -cnotmatch 'Usage') {
    $failures += '[hop 2] ClaudePromptRunner never mentions Usage in real code - the parsed usage is not carried onto PromptResult, so it stops at the parser and nothing downstream can ever read it. Add it beside the existing CostUsd = result.CostUsd mapping'
}

# --- NEGATIVE (#176): the severed hop must NOT be attempted from here --------------------------
# Not a style preference. AttemptJournaler cannot be made to work from this scope, so an edit there
# is either dead code or an out-of-scope escape the write-scope check rejects anyway - and either way
# it means the agent went hunting for a way to satisfy a clause that no longer exists.
$journaler = Read-Code 'src/Guardrails.Core/Execution/AttemptJournaler.cs'
if ($null -ne $journaler -and $journaler -cmatch 'Usage') {
    $failures += '[out of scope] AttemptJournaler.cs references Usage - journalling is NOT this task''s hop, and AttemptJournaler.cs is not in its writeScope. The datum cannot reach AttemptRecord from here at all: the journaler reads an ActionRun, which has no Usage member. A separate task owns ActionRunner.cs / RunReport.cs / Scheduler.cs and lands it properly'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== usage-token wiring: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "the usage datum must travel parser -> PromptResult. A gap at either hop leaves the token axis empty, and a costless local provider then reports no evidence of what it did at all."
    exit 1
}
exit 0
