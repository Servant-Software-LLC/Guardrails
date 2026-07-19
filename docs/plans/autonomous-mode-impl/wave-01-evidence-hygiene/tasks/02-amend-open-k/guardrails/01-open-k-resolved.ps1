# catches: the §10 K amendment claimed done but Open K was not actually rescoped to audit-only in
#          docs/plans/12-autonomous-mode.md. Greps for phrases the AMENDMENT adds ("resolved by #366",
#          "audit-only") — NOT tokens (autonomy.reviewGate / enforce) the pre-existing §10 K already
#          carried, which would pass trivially. Scoped to the one file this task owns.
$doc = "docs/plans/12-autonomous-mode.md"
if (-not (Test-Path $doc)) {
    Write-Output "$doc does not exist"
    exit 1
}
$content = Get-Content -Raw -Path $doc
if ($content -notmatch 'resolved by #366') {
    Write-Output "$doc §10 K does not contain 'resolved by #366' — Open K was not amended"
    exit 1
}
if ($content -notmatch 'audit-only') {
    Write-Output "$doc §10 K does not contain 'audit-only' — Open K was not rescoped"
    exit 1
}
exit 0
