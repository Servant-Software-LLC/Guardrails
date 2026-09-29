#Requires -Version 7
# Lite validate dispatcher - a complete, representative opening of a correct kernel script.
param([string]$PlanDir)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $PlanDir -or -not (Test-Path -LiteralPath $PlanDir -PathType Container)) {
    '{"script":"validate","ok":false,"error":"plan folder not found"}'
    exit 64
}

Import-Module (Join-Path $PSScriptRoot 'validate/Load.psm1') -Force
$r = Invoke-LiteLoad -PlanDir $PlanDir
$errors = @($r.Diagnostics | Where-Object { $_.severity -eq 'error' })
[pscustomobject]@{ script = 'validate'; ok = ($errors.Count -eq 0); diagnostics = @($r.Diagnostics) } |
    ConvertTo-Json -Depth 6 -Compress
if ($errors.Count -gt 0) { exit 1 }
exit 0
