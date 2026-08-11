# catches: tautological tests that already pass against the blind scanner - they would certify the
#          detection exists when nothing was added.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ClaudePermissionScannerBashRefusalTests" 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Output "the authored Bash-refusal tests PASS against the current scanner - they assert nothing new; make them encode the missing detection so they fail before it is implemented"
    exit 1
}
exit 0
