# catches: the diagnostic-code allocation was skipped or wrong - the fresh GR2015-GR2020 codes
#          that plan 08 mints (git/MAX_PATH/gate/empty-set/scope-escape/vacuous-scope) must be
#          documented in the SSOT, since GR2013/GR2014 stay taken by the (now-retired) triad
$ssot = "docs/plans/02-schemas-and-contracts.md"
$text = Get-Content $ssot -Raw
$codes = @('GR2015', 'GR2016', 'GR2017', 'GR2018', 'GR2019', 'GR2020')
$missing = @()
foreach ($c in $codes) {
    if ($text -notmatch $c) { $missing += $c }
}
if ($missing.Count -gt 0) {
    Write-Output "$ssot does not allocate the fresh diagnostic code(s): $($missing -join ', ')"
    exit 1
}
exit 0
