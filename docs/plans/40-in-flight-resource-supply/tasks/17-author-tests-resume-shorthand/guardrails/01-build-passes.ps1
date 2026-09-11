# catches: a test file that does not COMPILE being accepted as TDD "red". A non-compiling
#          test project exits non-zero from dotnet test identically to one that compiles and
#          fails, so without this the next guardrail cannot tell garbage from a real red —
#          and the implementation task (whose writeScope EXCLUDES the test file) could not
#          fix the compile error anyway, dead-ending the run (#155).
#          Subject: tests/Guardrails.Integration.Tests/Supply/SupplyResumeShorthandTests.cs
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output "The test project does not compile — the TDD red must COMPILE and fail, not fail to build."
    exit 1
}
exit 0
