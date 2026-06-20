# catches: the seam implemented wrong - WorktreeProviderSeamTests still failing (missing member,
#          wrong ExecuteAsync signature, bare-TaskNode channel not replaced, no overlap)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~WorktreeProviderSeamTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "WorktreeProviderSeamTests failing - the IWorktreeProvider seam / channel envelope is not implemented to spec"
    exit 1
}
exit 0
