# catches: the SSOT was not updated to document the new provenance field alongside the code (invariant 4).
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$path = Join-Path $ws 'docs/plans/02-schemas-and-contracts.md'
if (-not (Test-Path $path)) {
    Write-Output 'docs/plans/02-schemas-and-contracts.md does not exist.'
    exit 1
}
$content = Get-Content -Raw -Path $path
if (($content -notmatch 'resolvedModel')) {
    Write-Output 'SSOT 02-schemas-and-contracts.md does not mention resolvedModel - document the provenance contract in the same change as the code.'
    exit 1
}
exit 0
