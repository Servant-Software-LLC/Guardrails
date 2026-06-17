# catches: a regenerated golden example that does not actually load/validate/round-trip clean
#          against the new harness (e.g. a writeScope that trips GR2015/2016/2017, or a malformed
#          folder). The GoldenRoundTripTests meta-test is the skill's executable proof; run exactly
#          it (filtered), not the whole suite.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~GoldenRoundTrip" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "GoldenRoundTrip meta-test is failing - the regenerated golden example does not validate/round-trip clean under the new writeScope harness."
    exit 1
}
exit 0
