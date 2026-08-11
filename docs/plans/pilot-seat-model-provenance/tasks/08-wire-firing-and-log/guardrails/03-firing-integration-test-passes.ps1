# catches: an implementation whose behavior deviates from spec. #179 re-emit so the WHY reaches the tail.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
Set-Location $ws
$out = dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~ModelProvenanceFiringTests" --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' | ForEach-Object { $_.Line } | Select-Object -First 40
    Write-Output ''
    Write-Output '=== Failure details (re-emitted for the retry tail) ==='
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } } else { Write-Output '(no assertion lines matched - see full log above)' }
    Write-Output 'ModelProvenanceFiringTests still failing (see above).'
    exit 1
}
exit 0
