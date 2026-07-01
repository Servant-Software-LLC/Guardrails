# Bucket B — negative / assert-absent baseline. ONE-SHOT at run start; inverted polarity
# (FAILS if the RequestId field is already present). A no-op-root doctrine TASK with an
# ordinary scope:"local" guardrail; doctrine that SHIPS and VALIDATES today.
#
# catches: a TAUTOLOGY / false attribution. The charge result must NOT already expose a
# RequestId before this plan runs. If it does (someone added it already, or a stale build has
# it), then a later "the charge result exposes RequestId" gate proves NOTHING about whether
# THIS plan did the work. Proving absence NOW makes the later "present" gate provably this
# plan's own doing.
#
# POLARITY: NEGATIVE — this is the INVERTED case. exit 0 when the thing is ABSENT (the good
# starting state); exit 1 when it is already present.
#
# WHY scope:"local" / one-shot, and NEVER a union/terminal gate (#165/#132 union-inversion):
# a negative check is RED *after* the work is merged (the field IS present then). A negative
# polarity is inherently a "before the whole run" claim — true at the run's START and false
# ever after. So it runs ONCE, at the start, and scope:"local" keeps it OUT of the integration
# union re-verify set and the terminal gate. Re-running it post-merge would false-halt (the
# #165/#132 lesson). This is exactly WHY Bucket B is one-shot-only and FORBIDDEN at per-task
# scope (docs/plans/09-preflight-first-class.md §"Bucket B").
#
# CROSS-REFERENCE — do NOT fork: this is the SAME idea as the plan-breakdown anti-tautology
# archetype `tests-fail-on-current-code` / `tests-fail-on-stubs` (prove the new test is red
# against current code so a later green proves the implementation did it). A negative baseline
# is the GENERALIZATION of that archetype to NON-test artifacts (here: a field on a result
# object, not a test) — a cross-reference to it, NOT a rival mechanism (the design's
# determination point 4).
$ErrorActionPreference = 'Stop'

# In a real plan this would assert the field does NOT yet exist on the current code — e.g.:
#   if (Select-String -Path 'Acme.Payments.Core/ChargeResult.cs' -Pattern 'RequestId' -Quiet) {
#     Write-Output 'RequestId already present on ChargeResult — cannot attribute it to this plan'
#     exit 1
#   }
# SIMULATED here as a fixed pass (the field is absent on current code).
Write-Output "ChargeResult.RequestId absence baseline: absent on current code (simulated)"
exit 0
