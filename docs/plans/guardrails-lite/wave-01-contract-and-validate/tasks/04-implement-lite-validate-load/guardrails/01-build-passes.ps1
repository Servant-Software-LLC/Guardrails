# catches: a tree where the integration test project no longer builds, so the tests-pass check below
#          (which runs --no-build) would execute stale binaries or none at all. The implementation writes
#          only .ps1/.psm1, so this should stay green; it exists to make the next check's --no-build honest.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - read the compiler errors above; this task writes only scripts/lite/, so a build break means the base is broken (escalate as blocked-work, do not edit tests)"
    exit 1
}
exit 0
