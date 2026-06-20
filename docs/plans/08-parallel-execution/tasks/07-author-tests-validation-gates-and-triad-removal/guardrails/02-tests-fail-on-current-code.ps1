# catches: tautological gate tests - ParallelValidationGateTests that already pass against current
#          code, which has none of the new gates (GR2015/2016/2017/2018) and still runs the triad
#          validators. They must fail now and only pass once M2 implements the gates + tears down the
#          triad.
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~ParallelValidationGateTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "ParallelValidationGateTests PASS against current code - they are tautological (the M2 gates / triad teardown do not exist yet)"
    exit 1
}
exit 0
