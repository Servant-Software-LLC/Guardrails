#Requires -Version 7
param([Parameter(Mandatory)][string]$PlanDir)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib/Hash.psm1') -Force
[ordered]@{ script = 'plan-hash'; ok = $true; planDefinitionHash = (Get-LitePlanDefinitionHash -PlanDir $PlanDir) } | ConvertTo-Json -Compress
exit 0
