# catches: the pre-DAG phase landing WITHOUT its SSOT §7 resume-rule edit (invariant 4). Lower-bound
#          file-contains that §7 documents the planPreflights marker + the resume SKIP rule. Scoped to the
#          one doc this task owns.
$ssot = Get-Content "docs/plans/02-schemas-and-contracts.md" -Raw
if ($ssot -notmatch 'planPreflights') {
    Write-Output "02-schemas-and-contracts.md does not document the planPreflights journal section/resume rule - the SSOT §7 edit did not land with the pre-DAG phase (invariant 4)"
    exit 1
}
exit 0
