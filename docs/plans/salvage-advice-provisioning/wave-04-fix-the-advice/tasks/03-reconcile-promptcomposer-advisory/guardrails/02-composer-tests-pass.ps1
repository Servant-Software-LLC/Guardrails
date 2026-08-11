# catches: the advisory rewrite leaving its pinned test assertion stale, so the suite red-halts.
$log = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~PromptComposerTests" 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    foreach ($line in ($log -split "`r?`n")) { if ($line -match 'error|Assert\.|Exception|\[FAIL\]|at Guardrails') { Write-Output $line } }
    Write-Output "PromptComposerTests fail after the advisory rewrite"
    exit 1
}
exit 0
