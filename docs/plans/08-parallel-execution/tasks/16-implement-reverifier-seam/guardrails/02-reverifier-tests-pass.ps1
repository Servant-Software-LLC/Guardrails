# catches: a re-verify seam that secretly still depends on an attempt lifecycle / action result -
#          ReVerifierSeamTests still failing (the attempt-decoupled property is the load-bearing one)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~ReVerifierSeamTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "ReVerifierSeamTests failing - the attempt-decoupled IReVerifier seam is not implemented to spec"
    exit 1
}
exit 0
