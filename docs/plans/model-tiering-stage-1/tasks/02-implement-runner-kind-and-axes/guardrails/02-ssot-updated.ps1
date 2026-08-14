# catches: a schema change that ships WITHOUT its SSOT section 9 update - the plan requires prose and the
#          canonical-schema sentinel to land in the SAME change, and a green build says nothing about docs.
$ErrorActionPreference = 'Stop'
$ssot = 'docs/plans/02-schemas-and-contracts.md'
if (-not (Test-Path $ssot)) { Write-Output "$ssot not found"; exit 1 }
$c = Get-Content -Raw -Path $ssot
$missing = @()
foreach ($tok in @('costly','strength','specialization')) {
    if ($c -notmatch [regex]::Escape($tok)) { $missing += $tok }
}
if ($missing.Count -gt 0) {
    Write-Output ("$ssot does not document the new axes: " + ($missing -join ', ') + " - SSOT section 9 must land in the same change as the code")
    exit 1
}
exit 0
