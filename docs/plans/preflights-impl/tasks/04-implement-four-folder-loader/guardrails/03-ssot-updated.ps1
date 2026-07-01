# catches: the harness change landing WITHOUT its SSOT edit (invariant 4 - code and contract must never
#          disagree, and must land in the SAME change). Lower-bound file-contains that §1 layout names the
#          new folders and the GR-code table documents GR2027+. Scoped to the one doc this task owns.
$ssot = Get-Content "docs/plans/02-schemas-and-contracts.md" -Raw
if ($ssot -notmatch '<plan>/preflights/|preflights/') {
    Write-Output "02-schemas-and-contracts.md §1 layout does not mention the new plan-level preflights/ folder - the SSOT edit did not land with the loader change (invariant 4)"
    exit 1
}
if ($ssot -notmatch 'GR2027') {
    Write-Output "02-schemas-and-contracts.md does not document GR2027 - the new four-folder diagnostic codes are undocumented in the SSOT (invariant 4)"
    exit 1
}
exit 0
