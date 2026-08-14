# catches: a merged plan branch that does not compile - each task built in isolation but the union does not.
$ErrorActionPreference = 'Stop'
$log = & dotnet build Guardrails.sln --nologo -v q 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Output $_ }
if ($code -ne 0) {
    Write-Output ""
    Write-Output "--- build failure detail ---"
    $log | Select-String -Pattern 'error [A-Z]{2}[0-9]{4}' | ForEach-Object { Write-Output $_.Line }
    Write-Output "The merged plan branch does not build."
    exit 1
}
exit 0
