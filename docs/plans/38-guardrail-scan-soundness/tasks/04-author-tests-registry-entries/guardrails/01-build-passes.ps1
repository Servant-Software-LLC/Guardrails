# catches: a test file that does not COMPILE. Without this, a non-zero `dotnet test` in guardrail 02
#          would be ambiguous - garbage that fails to build exits non-zero identically to tests that
#          compile and fail, so a broken file would read as TDD red (#155). With the build green,
#          02's non-zero exit unambiguously means the tests RAN and FAILED.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # a non-zero dotnet exit is DATA here, not an error

$out = dotnet build tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --nologo -v q 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }
if ($buildExit -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not compile - BannedPatternRegistryTests.cs has a build error (see above). A non-compiling test file is NOT a TDD red."
    exit 1
}
exit 0
