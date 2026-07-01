# catches: the terminal phase landing WITHOUT its SSOT §7.1 edit (invariant 4). Lower-bound file-contains
#          that the reserved synthetic revalidate id is documented. Scoped to the one doc this task owns.
$ssot = Get-Content "docs/plans/02-schemas-and-contracts.md" -Raw
if ($ssot -notmatch 'plan:guardrails') {
    Write-Output "02-schemas-and-contracts.md §7.1 does not document --revalidate-task plan:guardrails - the SSOT edit did not land with the terminal phase (invariant 4)"
    exit 1
}
exit 0
