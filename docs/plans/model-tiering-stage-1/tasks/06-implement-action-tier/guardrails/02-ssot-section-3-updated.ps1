# catches: an action.tier change that ships WITHOUT its SSOT section 3 update - the plan requires code and
#          SSOT to land in the SAME change, and a green suite says nothing about docs.
#          Keyed on `action.tier`, NOT a bare "tier": the SSOT already uses "tier" a dozen times for the
#          scheduler's own wave tiers, so the bare-token version of this check passed before the task had
#          done anything at all.
$ErrorActionPreference = 'Stop'
$ssot = 'docs/plans/02-schemas-and-contracts.md'
if (-not (Test-Path $ssot)) { Write-Output "$ssot not found"; exit 1 }
$c = Get-Content -Raw -Path $ssot

if ($c -notmatch 'action\.tier') {
    Write-Output "$ssot does not document action.tier - SSOT section 3 must land in the same change as the code (a bare 'tier' does not count: the SSOT already uses that word for the scheduler's wave tiers)"
    exit 1
}

# The field alone is not the contract - its accepted values are. Proximity-scoped, because 'easy' and
# 'hard' each appear dozens of times elsewhere in this document for unrelated reasons.
$window = [regex]::Match($c, '(?s)action\.tier.{0,400}')
if (-not $window.Success -or $window.Value -notmatch '(?s)easy.{0,80}medium.{0,80}hard') {
    Write-Output "$ssot mentions action.tier but does not document its accepted values (easy/medium/hard) near it - the field without its value set is not the schema contract"
    exit 1
}
exit 0
