# catches: a WriteScope implementation whose overlap behavior deviates from the authored
#          truth-table (empty not disjoint-from-universal, sibling-prefix false-overlap,
#          dir != dir/**, etc.). Filtered to THIS milestone's tests, not the whole suite.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~WriteScope" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "WriteScope tests are failing - the overlap function is not implemented to the truth-table spec."
    exit 1
}
exit 0
