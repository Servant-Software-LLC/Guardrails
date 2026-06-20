# catches: code that doesn't compile - the new seam types or the changed ExecuteAsync signature
#          left Guardrails.Core in a non-building state
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after the worktree-provider seam change"
    exit 1
}
exit 0
