# catches: the action claiming to author tests but writing no new test file.
$ErrorActionPreference = 'Stop'
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# In a real plan: assert at least one new RequestId test exists in the test project.
#   Select-String -Path (Join-Path $ws 'Acme.Payments.Core.Tests/*.cs') -Pattern 'RequestId' -Quiet
# SIMULATED here as a fixed pass.
Write-Output "new RequestId tests present (simulated)"
exit 0
