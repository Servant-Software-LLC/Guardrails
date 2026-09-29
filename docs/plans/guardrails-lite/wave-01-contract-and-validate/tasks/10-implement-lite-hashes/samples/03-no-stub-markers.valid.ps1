#Requires -Version 7
# plan-hash.ps1 (sample) - the task-09 stub used to print "stub":true and exit 99; this comment is legal.
param([string]$PlanDir)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $PlanDir -or -not (Test-Path -LiteralPath $PlanDir)) {
    [Console]::Error.WriteLine("plan-hash: plan folder not found: $PlanDir")
    [pscustomobject]@{ script = 'plan-hash'; ok = $false } | ConvertTo-Json -Compress
    exit 64
}
Import-Module (Join-Path $PSScriptRoot 'lib/Hash.psm1')
$tasks = [ordered]@{}
[pscustomobject]@{
    script             = 'plan-hash'
    ok                 = $true
    planDefinitionHash = Get-LitePlanDefinitionHash -PlanDir $PlanDir
    planHash           = Get-LiteNarrowPlanHash -PlanDir $PlanDir
    tasks              = $tasks
} | ConvertTo-Json -Compress
exit 0
