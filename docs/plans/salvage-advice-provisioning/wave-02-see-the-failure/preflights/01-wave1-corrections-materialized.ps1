# catches: wave 2 starting on a HEAD where wave 1's SSOT correction did not actually land, so this wave
#          builds on a contract that still names the ungranted recovery route.
# POSITIVE / monotone-safe by design (#254): asserts the corrected artifact is PRESENT, never that the
# old text is absent. Wave 1's own 01-false-claims-removed guardrail already covers the removal, and a
# wave-entry negative assertion is the polarity the waved doctrine warns against.
$s = 'docs/plans/02-schemas-and-contracts.md'
if (-not (Test-Path $s)) { Write-Output "$s is missing on the merged HEAD"; exit 1 }
if ((Get-Content -Raw -Path $s) -notmatch 'git show <ref>:<path>') {
    Write-Output "$s does not yet name the corrected SOME-recovery route 'git show <ref>:<path>' - wave 1's SSOT fix did not materialize"
    exit 1
}
exit 0
