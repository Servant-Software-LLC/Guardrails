# catches: the gates / triad teardown implemented wrong - ParallelValidationGateTests still failing
#          (a missing GR code, a gate that does not fire, or the triad validators still running)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~ParallelValidationGateTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "ParallelValidationGateTests failing - the M2 validation gates / triad teardown are not implemented to spec"
    exit 1
}
exit 0
