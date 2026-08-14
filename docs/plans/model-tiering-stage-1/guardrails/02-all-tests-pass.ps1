# catches: a merged plan branch whose suite is red - every task green in isolation, the union broken.
$ErrorActionPreference = 'Stop'
$log = & dotnet test Guardrails.sln --nologo -v q 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Output $_ }
if ($code -ne 0) {
    Write-Output ""
    Write-Output "--- test failure detail ---"
    $log | Select-String -Pattern '^\s*(Failed|Error Message|Assert\.)' | ForEach-Object { Write-Output $_.Line }
    Write-Output "The merged plan branch has failing tests."
    exit 1
}
exit 0
