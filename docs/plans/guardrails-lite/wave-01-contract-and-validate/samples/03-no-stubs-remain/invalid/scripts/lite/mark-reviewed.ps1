#Requires -Version 7
param([Parameter(Mandatory)][string]$PlanDir)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib/Hash.psm1') -Force
$hash = Get-LitePlanDefinitionHash -PlanDir $PlanDir
$marker = [ordered]@{ version = 2; reviewedAt = (Get-Date).ToUniversalTime().ToString('o'); planHash = $hash; attestation = @{ source = 'bare'; tool = 'guardrails-lite 0.0.0-dev' } }
New-Item -ItemType Directory -Force -Path (Join-Path $PlanDir 'state') | Out-Null
$marker | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $PlanDir 'state/guardrails-review.json')
[ordered]@{ script = 'mark-reviewed'; ok = $true; planHash = $hash } | ConvertTo-Json -Compress
exit 0
