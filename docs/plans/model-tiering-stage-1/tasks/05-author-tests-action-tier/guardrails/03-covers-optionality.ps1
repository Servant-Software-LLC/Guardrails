# catches: tier tests that cover only the happy path. The plan's load-bearing guarantee is that tier is
#          OPTIONAL - a plan with no tier must parse and validate exactly as today - and a suite that never
#          asserts the untagged case would go green over a change that broke every existing plan.
$ErrorActionPreference = 'Stop'
$file = 'tests/Guardrails.Core.Tests/ModelTiering/ActionTierTests.cs'
if (-not (Test-Path $file)) { Write-Output "$file was not created"; exit 1 }
$c = Get-Content -Raw -Path $file
if ($c -notmatch '\[(Fact|Theory)\]') { Write-Output "$file declares no [Fact]/[Theory]"; exit 1 }
$missing = @()
foreach ($tok in @('easy','medium','hard')) {
    if ($c -notmatch [regex]::Escape($tok)) { $missing += $tok }
}
if ($missing.Count -gt 0) {
    Write-Output ("$file does not cover the tier values: " + ($missing -join ', '))
    exit 1
}
if ($c -notmatch '(?i)(optional|no\s*tier|untagged|default)') {
    Write-Output "$file never asserts the OPTIONAL/untagged case - the additive guarantee (a plan with no tier behaves as today) is unguarded"
    exit 1
}
exit 0
