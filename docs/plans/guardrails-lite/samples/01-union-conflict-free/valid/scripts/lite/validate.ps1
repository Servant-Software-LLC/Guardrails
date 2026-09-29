#Requires -Version 7
# ==========================================================================
# A clean, merged validate.ps1. The banner above and the setext-style rule
# below must NOT trip the union gate (#187: only column-0 <<<<<<< / >>>>>>>).
# ==========================================================================
param([string]$PlanDir)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$result = [ordered]@{ script = 'validate'; ok = $true; diagnostics = @() }
$result | ConvertTo-Json -Compress -Depth 5
exit 0
