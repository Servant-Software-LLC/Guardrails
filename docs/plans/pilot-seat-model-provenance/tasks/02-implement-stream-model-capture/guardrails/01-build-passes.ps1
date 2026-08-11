# catches: the change does not compile.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
Set-Location $ws
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output 'src/Guardrails.Core does not build - see errors above.'
    exit 1
}
exit 0
