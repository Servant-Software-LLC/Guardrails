# catches: the merged plan-branch HEAD does not compile. Terminal postcondition: LOCAL scope (no scope
#          key) so it runs ONCE on the fully merged HEAD, never at an intermediate union.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
Set-Location $ws
dotnet build Guardrails.sln -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output 'Guardrails.sln does not build on the merged HEAD - see the compiler errors above.'
    exit 1
}
exit 0
