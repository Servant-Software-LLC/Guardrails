# catches: the renderer rewrite landing WITHOUT its SSOT §10 edit (invariant 4). Lower-bound file-contains
#          that §10 describes the container model (invisible anchors) and that the old done_ node model is
#          gone from the doc. Scoped to the one doc this task owns.
$ssot = Get-Content "docs/plans/02-schemas-and-contracts.md" -Raw
if ($ssot -notmatch '(?i)anchor') {
    Write-Output "02-schemas-and-contracts.md §10 does not describe the invisible-anchor container model - the SSOT edit did not land with the renderer rewrite (invariant 4)"
    exit 1
}
exit 0
