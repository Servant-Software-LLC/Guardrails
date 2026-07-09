# Deterministic action: writes the recipient config the second stage reads.
# Reads the seeded recipient name from GUARDRAILS_STATE_IN, falling back to "World".
$ErrorActionPreference = "Stop"

$name = "World"
if ($env:GUARDRAILS_STATE_IN -and (Test-Path $env:GUARDRAILS_STATE_IN)) {
    $state = Get-Content -Raw -Path $env:GUARDRAILS_STATE_IN | ConvertFrom-Json
    if ($state.recipientName) { $name = [string]$state.recipientName }
}

New-Item -ItemType Directory -Force "out" | Out-Null
@{ name = $name } | ConvertTo-Json | Set-Content -Path "out/config.json" -Encoding utf8

Write-Output "wrote out/config.json for '$name'"
exit 0
