# Deterministic action: writes the greeting script the rest of the plan uses.
# cwd is the workspace (examples/hello-guardrails/), per the harness contract.
$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force "out" | Out-Null

@'
param([Parameter(Mandatory = $true)][string]$Name)
Write-Output "Hello, $Name!"
'@ | Set-Content -Path "out/greet.ps1" -Encoding utf8

Write-Output "wrote out/greet.ps1"
exit 0
