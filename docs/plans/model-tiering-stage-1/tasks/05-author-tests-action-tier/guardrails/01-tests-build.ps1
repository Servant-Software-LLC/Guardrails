# catches: a "red" that is really a COMPILE error - unfixable by the implementation task, whose writeScope
#          excludes the test file.
$ErrorActionPreference = 'Stop'
$log = & dotnet build tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --nologo -v q 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Output $_ }
if ($code -ne 0) {
    Write-Output ""
    $log | Select-String -Pattern 'error [A-Z]{2}[0-9]{4}' | ForEach-Object { Write-Output $_.Line }
    Write-Output "The authored tests do not COMPILE - add the minimal stubs; a non-compiling test file is not TDD red."
    exit 1
}
exit 0
