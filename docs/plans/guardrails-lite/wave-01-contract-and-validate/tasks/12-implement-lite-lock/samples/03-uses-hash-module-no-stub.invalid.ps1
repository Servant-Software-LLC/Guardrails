#Requires -Version 7
# lock.ps1 (sample) - the ONE defect: it hashes inline instead of importing Hash.psm1.
param([string]$PlanDir, [switch]$Force, [switch]$Verify)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $PlanDir -or -not (Test-Path -LiteralPath $PlanDir)) { exit 64 }
function Get-InlineHash([string]$text) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    'sha256:' + [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}
$planHash = Get-InlineHash ([System.IO.File]::ReadAllText((Join-Path $PlanDir 'guardrails.json')))
$state = Join-Path $PlanDir 'state'
New-Item -ItemType Directory -Force -Path $state | Out-Null
@{ version = 1; planDefinitionHash = $planHash; tasks = @{} } | ConvertTo-Json | Set-Content (Join-Path $state 'lite-run.lock.json')
[pscustomobject]@{ script = 'lock'; ok = $true; planDefinitionHash = $planHash } | ConvertTo-Json -Compress
exit 0
