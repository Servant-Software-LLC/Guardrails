# Deterministic action: writes a short report quoting the generated greeting.
# Intra-wave dependency: 01-generate-greeting (a plain sibling folder name — cross-wave edges are
# forbidden, GR2034; the wave barrier already orders wave-01 before wave-02).
$ErrorActionPreference = "Stop"

$greeting = (Get-Content -Raw -Path "out/greeting.txt").Trim()

@"
# Greeting report

The generated greeting was:

> $greeting

It follows the required 'Hello, <name>!' shape.
"@ | Set-Content -Path "out/report.md" -Encoding utf8

Write-Output "wrote out/report.md"
exit 0
