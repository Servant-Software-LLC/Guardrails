# catches: a regression anywhere in the repo. Terminal postcondition: the WHOLE suite on the merged
#          HEAD, LOCAL scope (runs ONCE at the end, never at a partial union - the #125/#165 anti-
#          pattern). #179 re-emit so a red WHY reaches the halt feedback tail.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
Set-Location $ws
$out = dotnet test Guardrails.sln --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' | ForEach-Object { $_.Line } | Select-Object -First 40
    Write-Output ''
    Write-Output '=== Failure details (re-emitted for the halt feedback tail) ==='
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } } else { Write-Output '(no assertion lines matched - see full log above)' }
    Write-Output 'The full suite is red on the merged HEAD (see failure details above).'
    exit 1
}
exit 0
