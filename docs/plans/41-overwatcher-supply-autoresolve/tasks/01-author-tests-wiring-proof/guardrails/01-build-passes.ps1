# catches: a test file that does not COMPILE being accepted as TDD "red". A non-compiling test
#          project exits non-zero from dotnet test identically to one that compiles and fails, so
#          without this the next guardrail cannot tell garbage from a real red — and the
#          implementation task (task 14, whose writeScope EXCLUDES this test file) could not fix the
#          compile error anyway, dead-ending the run (#155).
#          This file has NO dependencies (task.json: "dependsOn": []), so it is built against the
#          CURRENT tree: a reference to any symbol this plan has not authored yet — Certify,
#          SupplyCertification, MissingResourceFacts, GateThreshold, DecisionTokens.AutoSupplied,
#          OverwatchTrigger.MissingResource, the 3-arg SuppliedResourcesCommitted — fails HERE.
#          Subject: tests/Guardrails.Integration.Tests/Supply/OverwatchSupplyAutoResolveWiringTests.cs
#          No required-present regex clause in this guardrail, so there is no #478 baseline to census.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output "The test project does not compile — the TDD red must COMPILE and fail, not fail to build. If the break is a symbol this plan has not authored yet, assert the WIRE TOKEN as a string literal instead of referencing the type."
    exit 1
}
exit 0
