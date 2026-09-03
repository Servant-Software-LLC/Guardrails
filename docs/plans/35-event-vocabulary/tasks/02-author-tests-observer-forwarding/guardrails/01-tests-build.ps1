# catches: a "red" that is really a BROKEN test. A test file that does not COMPILE makes dotnet test
#          exit non-zero identically to one that compiles and fails, so without this the next
#          guardrail would accept garbage as TDD red - and the implementation task (whose writeScope
#          excludes the test file) could not fix the compile error, dead-ending the run (#155).
$ErrorActionPreference = 'Continue'
$log = & dotnet build tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj -c Debug -v q --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Compile errors in the authored tests ==="
    $log -split "`r?`n" | Where-Object { $_ -match 'error [A-Z]{2}\d+' } | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "The integration test project does not compile. A TDD red must COMPILE and fail - a test that cannot build is a broken test, not a red one."
    exit 1
}
Write-Output "Integration test project compiles."
exit 0
