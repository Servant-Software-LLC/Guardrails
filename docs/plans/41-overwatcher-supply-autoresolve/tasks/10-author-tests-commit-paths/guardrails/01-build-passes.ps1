# catches: a test file that does not COMPILE being accepted as TDD "red". A non-compiling test
#          project exits non-zero from dotnet test identically to one that compiles and fails, so
#          without this the census below cannot tell garbage from a real red — and task 11 (whose
#          writeScope EXCLUDES the test file) could not fix the compile error anyway, dead-ending
#          the run (#155).
#          Subject: tests/Guardrails.Core.Tests/Supply/SuppliedDrainTests.cs plus the CommitPaths
#          stub on src/Guardrails.Core/Execution/SuppliedDrain.cs. The SOLUTION is built, not the one
#          project: the stub and its test live in different projects, and the pinned signature has to
#          hold for both.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output "The solution does not compile — the TDD red must COMPILE and fail, not fail to build. Check the CommitPaths stub's signature against the one pinned in the prompt."
    exit 1
}
exit 0
