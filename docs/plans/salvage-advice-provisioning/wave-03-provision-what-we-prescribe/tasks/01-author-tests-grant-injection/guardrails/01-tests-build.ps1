# catches: a test file that does not COMPILE - which exits non-zero identically to a real TDD red, so
#          garbage would pass as "failing tests" and the implementation task could never fix it.
$log = dotnet build tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $log
    foreach ($line in ($log -split "`r?`n")) { if ($line -match ': error ') { Write-Output $line } }
    Write-Output "the authored tests do not compile - a TDD red must COMPILE and fail, not fail to build"
    exit 1
}
exit 0
