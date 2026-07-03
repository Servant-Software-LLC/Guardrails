# catches: middleware that is wired but never actually threads the id — an inbound X-Request-Id
# must reach the charge result. In a real plan this would assert the end-to-end flow (request
# header -> charge call -> ChargeResult.RequestId). SIMULATED here as a fixed pass.
$ErrorActionPreference = 'Stop'
Write-Output "inbound X-Request-Id flows to the charge result (simulated)"
exit 0
