# catches: the SSOT edit claimed-done but the new contract sections (write-scope check,
#          guardrail scope, AI-merge worker) were never written into 02-schemas-and-contracts.md
$ssot = "docs/plans/02-schemas-and-contracts.md"
if (-not (Test-Path $ssot)) {
    Write-Output "$ssot does not exist"
    exit 1
}
$text = Get-Content $ssot -Raw
$needles = @('writeScope', 'integrationGate', 'mergeOnSuccess', 'AI-merge')
$missing = @()
foreach ($n in $needles) {
    if ($text -notmatch [regex]::Escape($n)) { $missing += $n }
}
if ($missing.Count -gt 0) {
    Write-Output "$ssot is missing the required contract term(s): $($missing -join ', ')"
    exit 1
}
exit 0
