#Requires -Version 7
param([Parameter(Mandatory)][string]$PlanDir, [switch]$Verify, [switch]$Force)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib/Hash.psm1') -Force
$lockPath = Join-Path $PlanDir 'state/lite-run.lock.json'
$current = Get-LitePlanDefinitionHash -PlanDir $PlanDir
if ($Verify) {
    $lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
    $same = $lock.planDefinitionHash -eq $current
    [ordered]@{ script = 'lock'; ok = $same; changed = @(if (-not $same) { '<plan>' }) } | ConvertTo-Json -Compress
    exit $(if ($same) { 0 } else { 1 })
}
New-Item -ItemType Directory -Force -Path (Join-Path $PlanDir 'state') | Out-Null
[ordered]@{ version = 1; createdAt = (Get-Date).ToUniversalTime().ToString('o'); planDefinitionHash = $current; tasks = @{} } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $lockPath
[ordered]@{ script = 'lock'; ok = $true } | ConvertTo-Json -Compress
exit 0
