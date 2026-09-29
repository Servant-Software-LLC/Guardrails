#Requires -Version 7
param([string]$PlanDir)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
<<<<<<< HEAD
$result = [ordered]@{ script = 'validate'; ok = $true; diagnostics = @() }
=======
$result = [ordered]@{ script = 'validate'; ok = $false; diagnostics = @() }
>>>>>>> guardrails/guardrails-lite/wave-01-contract-and-validate/06-implement-lite-validate-graph
$result | ConvertTo-Json -Compress -Depth 5
exit 0
