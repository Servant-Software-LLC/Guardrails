# catches: a claimed doc update that never landed - the SSOT and/or the domain-knowledge
#          skill must actually mention maxCostUsd and the "cost cap reached" reason
$ssot = Get-Content "docs/plans/02-schemas-and-contracts.md" -Raw
if ($ssot -notmatch "maxCostUsd") {
  Write-Output "docs/plans/02-schemas-and-contracts.md does not mention maxCostUsd"
  exit 1
}
if ($ssot -notmatch "cost cap reached") {
  Write-Output "docs/plans/02-schemas-and-contracts.md does not document the 'cost cap reached' reason"
  exit 1
}
$skill = Get-Content ".claude/skills/guardrails-domain-knowledge/SKILL.md" -Raw
if ($skill -notmatch "maxCostUsd") {
  Write-Output "guardrails-domain-knowledge SKILL.md does not mention maxCostUsd"
  exit 1
}
exit 0
