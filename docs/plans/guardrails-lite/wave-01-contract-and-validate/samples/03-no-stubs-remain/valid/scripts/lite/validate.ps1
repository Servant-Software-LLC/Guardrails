#Requires -Version 7
# The TDD stub this replaced printed {"script":"validate","ok":false,"stub":true} and did exit 99.
# Those words live in a COMMENT here, so the no-stubs gate must NOT red on them (comment-blind).
<#
  Block comment, same rule: throw 'stub' / exit 99 / "stub": true
#>
param([Parameter(Mandatory)][string]$PlanDir)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'validate/Load.psm1') -Force
$r = Invoke-LiteLoad -PlanDir $PlanDir
$diagnostics = @($r.Diagnostics)
$hasError = @($diagnostics | Where-Object { $_.severity -eq 'error' }).Count -gt 0
[ordered]@{ script = 'validate'; ok = -not $hasError; diagnostics = $diagnostics } | ConvertTo-Json -Compress -Depth 6
exit $(if ($hasError) { 1 } else { 0 })
