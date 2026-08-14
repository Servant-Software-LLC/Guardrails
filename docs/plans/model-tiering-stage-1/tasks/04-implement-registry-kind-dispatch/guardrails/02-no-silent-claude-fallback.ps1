# catches: an implementation that satisfies the tests by DEFAULTING an unknown kind to Claude in some path
#          the tests do not reach - the plan forbids a silent fallback outright, and a green suite does not
#          prove the absence of a default branch. Scoped to the one file this task owns.
$ErrorActionPreference = 'Stop'
$file = 'src/Guardrails.Core/Prompts/PromptRunnerRegistry.cs'
if (-not (Test-Path $file)) { Write-Output "$file not found"; exit 1 }
$c = Get-Content -Raw -Path $file
$stripped = [regex]::Replace($c, '(?m)//.*$', '')
$stripped = [regex]::Replace($stripped, '(?s)/\*.*?\*/', '')
if ($stripped -match '(?s)(default|_)\s*(:|=>)\s*[^;{]{0,80}new\s+ClaudePromptRunner') {
    Write-Output "$file falls back to ClaudePromptRunner in a default/discard switch arm - the plan requires an unimplemented kind to FAIL with an actionable message, never a silent Claude fallback"
    exit 1
}
exit 0
