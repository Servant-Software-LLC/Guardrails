# catches: a test file that does not COMPILE being accepted as TDD "red". A non-compiling test
#          project exits non-zero from dotnet test identically to one that compiles and fails, so
#          without this the next guardrail cannot tell garbage from a real red — and the
#          implementation task (whose writeScope EXCLUDES the test file) could not fix the compile
#          error anyway, dead-ending the run (#155).
#          Subject: tests/Guardrails.Core.Tests/OverwatchResourceSupplyBriefTests.cs
#          The whole solution is built, not just the test project: this task adds an enum member to the
#          PUBLIC OverwatchTrigger and a method to Overwatch, and both must still compile for every
#          reader in Core and Cli.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output "The solution does not compile — the TDD red must COMPILE and fail, not fail to build. If the break is a missing MissingResourceCandidate type, that type belongs to task 03's MissingResourceFacts.cs: read it and match its shape rather than declaring a copy."
    exit 1
}
exit 0
