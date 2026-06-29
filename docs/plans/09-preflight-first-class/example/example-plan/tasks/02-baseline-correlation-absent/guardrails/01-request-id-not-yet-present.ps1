# scope: precondition  (SIMULATED — see the example/README.md and guardrails.json header)
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
# CROSS-REFERENCE — do NOT fork: this is the SAME idea as the plan-breakdown anti-tautology
# archetype `tests-fail-on-current-code` / `tests-fail-on-stubs` (prove the new test is red
# against current code so a later green proves the implementation did it). A first-class
# negative preflight is the GENERALIZATION of that archetype to NON-test artifacts (here: a
# field on a result object, not a test). The design is explicit that the two must never
# diverge — a negative preflight is the same `scope:"precondition"` guardrail with inverted
# pass/fail, NOT a rival mechanism (docs/plans/09-preflight-first-class.md §"Positive vs negative
# modeling" and the determination point 4).
#
# UNION-INVERSION SAFETY — BLOCKER (d): a negative check is RED *after* the work is merged (the
# field IS present then). That is exactly why a precondition is a pre-DAG ONE-SHOT, NEVER part
# of the integration union re-verify set and NEVER the terminal gate — re-running it post-merge
# would false-fail (the #165/#132 lesson). The scope:"precondition" set is separate from the
# integration set precisely so it is never re-run on merged bytes.
$ErrorActionPreference = 'Stop'

# In a real plan this would assert the field does NOT yet exist on the current code — e.g.:
#   if (Select-String -Path 'Acme.Payments.Core/ChargeResult.cs' -Pattern 'RequestId' -Quiet) {
#     Write-Output 'RequestId already present on ChargeResult — cannot attribute it to this plan'
#     exit 1
#   }
# SIMULATED here as a fixed pass (the field is absent on current code).
Write-Output "ChargeResult.RequestId absence baseline: absent on current code (simulated)"
exit 0
