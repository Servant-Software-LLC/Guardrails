# catches: the de-serialize change regressed the M1 seam behaviour - the overlap / channel-envelope
#          tests no longer pass after removing the capture seam and the exclusive gate
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~WorktreeProviderSeamTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "WorktreeProviderSeamTests failing after de-serialize / begin-triad-teardown - overlap or channel envelope regressed"
    exit 1
}
exit 0
