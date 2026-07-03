# catches: a TAUTOLOGY / false attribution. The RequestId field must NOT already exist on
# ChargeResult before this plan starts — if it already existed, a later "RequestId is present"
# gate would prove nothing about THIS plan's work. Proving its absence now makes any later
# presence provably this plan's doing, not pre-existing.
#
# POLARITY: negative / assert-absent — exit 0 when the field is ABSENT, exit 1 when it is already
# present. One-shot, plan-wide, before any task's first wave; this check is NEVER re-run at a
# union/terminal gate — a later, post-merge tree is EXPECTED to have the field present, so
# re-running this same assertion there would be wrong, not merely redundant.
$ErrorActionPreference = 'Stop'

# In a real plan this would grep the inherited source, e.g.:
#   if (Select-String -Path 'Acme.Payments.Core/ChargeResult.cs' -Pattern 'RequestId' -Quiet) {
#     Write-Output 'RequestId already present on ChargeResult before this plan started'
#     exit 1
#   }
# A byte-check on the committed source — NOT a live probe. SIMULATED here as a fixed pass — this
# is an illustrative sample plan, not wired to a real repo.
Write-Output "RequestId is absent from ChargeResult before this plan starts (simulated)"
exit 0
