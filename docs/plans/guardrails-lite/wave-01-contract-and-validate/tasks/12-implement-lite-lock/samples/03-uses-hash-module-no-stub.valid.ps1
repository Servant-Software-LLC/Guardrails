#Requires -Version 7
# lock.ps1 (sample). The lock records SHA-256 definition hashes; the hashing itself lives in Hash.psm1.
param([string]$PlanDir, [switch]$Force, [switch]$Verify)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $PlanDir -or -not (Test-Path -LiteralPath $PlanDir)) {
    [Console]::Error.WriteLine("lock: plan folder not found: $PlanDir")
    [pscustomobject]@{ script = 'lock'; ok = $false } | ConvertTo-Json -Compress
    exit 64
}
Import-Module (Join-Path $PSScriptRoot 'lib/Hash.psm1')
$ids = [System.Collections.Generic.List[string]]::new()
Get-ChildItem -LiteralPath (Join-Path $PlanDir 'tasks') -Directory | ForEach-Object { $ids.Add($_.Name) }
$ids.Sort([System.StringComparer]::Ordinal)
$tasks = [ordered]@{}
foreach ($id in $ids) { $tasks[$id] = Get-LiteTaskDefinitionHash -PlanDir $PlanDir -TaskId $id }
$lock = [ordered]@{
    version            = 1
    createdAt          = [DateTimeOffset]::UtcNow.ToString('o')
    planDefinitionHash = Get-LitePlanDefinitionHash -PlanDir $PlanDir
    tasks              = $tasks
}
$state = Join-Path $PlanDir 'state'
New-Item -ItemType Directory -Force -Path $state | Out-Null
$lock | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $state 'lite-run.lock.json') -Encoding utf8NoBOM
[pscustomobject]@{ script = 'lock'; ok = $true; planDefinitionHash = $lock.planDefinitionHash } | ConvertTo-Json -Compress
exit 0
