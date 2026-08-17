# catches: the prompt's explicit prohibition being ignored (#221 - a prose "do NOT" with no
#          structural backing is free to break). DoR 6.3: "No new probe enum is introduced in v1"
#          and no new PromptFailureKind member - a connection-level failure is the SHIPPED
#          PromptFailureKind.Transient, riding the SHIPPED #115 pause. An added `Unavailable` member
#          would be a second, parallel classification the retry policy does not route on, so it
#          would consume the retry budget silently while LOOKING like it paused.
#
#          Also guards the shared-file boundary the prompt draws: ProcessRunner.cs is shared with
#          script actions and guardrails and is deliberately outside this task's writeScope; the
#          launch-failure catch belongs in the Claude quarantine.
$ErrorActionPreference = 'Continue'
$failures = @()

# 1. The enum is untouched (it is not in this task's writeScope, so this is a cheap early read of the
#    same fact the write-scope check enforces - but with a message that says WHY).
$kindFile = 'src/Guardrails.Core/Prompts/PromptFailureKind.cs'
if (-not (Test-Path $kindFile)) {
    $failures += "$kindFile does not exist"
} else {
    $kind = Get-Content -Raw $kindFile
    if ($kind -match '(?m)^\s*Unavailable\s*[,}]?\s*$') {
        $failures += 'PromptFailureKind gained an `Unavailable` member - 6.3 forbids a new classification in v1. A connection-level failure is Transient, which is the ONLY value the retry policy treats as non-budget-consuming'
    }
}

# 2. The launch-failure handling landed in the vendor quarantine, not in the shared process runner.
$runner = 'src/Guardrails.Core/Prompts/ClaudePromptRunner.cs'
if (-not (Test-Path $runner)) {
    $failures += "$runner does not exist"
} else {
    $content = Get-Content -Raw $runner
    # A missing CLI throws out of process.Start() before any text exists to classify, so the runner
    # must CATCH it and route the message through the classifier. Structural: a catch clause naming a
    # launch-failure exception type, plus a Classify call.
    if ($content -notmatch 'catch\s*\(\s*(System\.ComponentModel\.)?Win32Exception') {
        $failures += 'ClaudePromptRunner has no `catch (Win32Exception ...)` around the process launch - a missing CLI throws out of ProcessRunner.Start() before any error text exists to classify, so the shape DoR 6.3 names ("a missing CLI") never reaches the quarantine at all'
    }
    if ($content -notmatch 'ClaudeSignalClassifier\.Classify\s*\(') {
        $failures += 'ClaudePromptRunner no longer routes a failure through ClaudeSignalClassifier.Classify - the runner-agnostic classification must come from the quarantine, never from an inline string test'
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== 6.3 boundary: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
