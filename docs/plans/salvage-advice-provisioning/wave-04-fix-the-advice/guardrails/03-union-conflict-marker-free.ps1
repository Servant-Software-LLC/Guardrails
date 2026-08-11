# catches: an AI-merge of wave 4's parallel advice-reconciliation tasks that leaves git conflict
#          markers in a shared source file - each task passed alone, but the union did not integrate.
$failures = @()
$files = @(
  'src/Guardrails.Core/Execution/RetryPolicy.cs',
  'src/Guardrails.Core/Prompts/PromptComposer.cs',
  'src/Guardrails.Core/Prompts/WorktreeContainmentHook.cs',
  'tests/Guardrails.Core.Tests/PromptComposerTests.cs',
  'tests/Guardrails.Core.Tests/RetryPolicySalvageAdviceTests.cs'
)
foreach ($f in $files) {
    if (-not (Test-Path $f)) { continue }          # union-safe: absent at this union is fine
    $content = Get-Content -Raw -Path $f
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures += "$f contains git conflict markers - the union did not cleanly integrate"
    }
    if ([string]::IsNullOrWhiteSpace($content)) {
        $failures += "$f is empty in the union - a contribution was lost"
    }
}
if ($failures.Count -gt 0) {
    Write-Output ($failures -join '; ')
    exit 1
}
exit 0
