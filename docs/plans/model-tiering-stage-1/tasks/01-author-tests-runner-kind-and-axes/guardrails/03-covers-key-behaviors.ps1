# catches: a test file that compiles and fails but covers only one behaviour - the plan names six, and a
#          single failing assertion satisfies the red check while leaving five requirements unguarded.
$ErrorActionPreference = 'Stop'
$file = 'tests/Guardrails.Core.Tests/ModelTiering/PromptRunnerSchemaTests.cs'
if (-not (Test-Path $file)) { Write-Output "$file was not created"; exit 1 }
$c = Get-Content -Raw -Path $file
if ($c -notmatch '\[(Fact|Theory)\]') {
    Write-Output "$file declares no [Fact]/[Theory] - it is not a test file"
    exit 1
}
$missing = @()
foreach ($tok in @('costly','strength','specialization','rank','routing','claude')) {
    if ($c -notmatch [regex]::Escape($tok)) { $missing += $tok }
}
if ($missing.Count -gt 0) {
    Write-Output ("$file does not cover: " + ($missing -join ', ') + " - the plan requires all six schema behaviours be encoded")
    exit 1
}
exit 0
