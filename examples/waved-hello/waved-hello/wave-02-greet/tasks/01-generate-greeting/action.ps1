# Deterministic action: runs the scaffolded greeting script for the configured name.
# Depends (via the wave barrier, not a dependsOn edge) on wave-01 having materialized
# out/greet.ps1 and out/config.json.
$ErrorActionPreference = "Stop"

$cfg = Get-Content -Raw -Path "out/config.json" | ConvertFrom-Json
$greeting = & "out/greet.ps1" -Name $cfg.name
$greeting | Set-Content -Path "out/greeting.txt" -Encoding utf8

Write-Output "wrote out/greeting.txt: $greeting"
exit 0
