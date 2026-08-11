# catches: tautological tests - with the build green (guardrail 01) a non-zero exit here means the
#          tests RAN and FAILED against the stubs = TDD red. A zero exit means they assert nothing.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
Set-Location $ws
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~ObserverModelResolvedTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output 'the ObserverModelResolvedTests PASS against the stubs - tautological (no real behavior asserted).'
    exit 1
}
exit 0
