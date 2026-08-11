# catches: a test that passes against the CURRENT (unwired) code - it would verify nothing. A non-zero
#          exit means the integration test FAILS before the wiring lands = TDD red.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
Set-Location $ws
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~ModelProvenanceFiringTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output 'the ModelProvenanceFiringTests PASS against the current unwired code - TDD red is not established.'
    exit 1
}
exit 0
