# catches: a TAUTOLOGICAL test — one that passes even though the implementation does not exist
# yet. The new RequestId tests MUST be RED against the current code; a later green then proves
# 04-implement-correlation did the work, not a vacuous test.
#
# This is the LOCAL polarity (scope:"local") that the 02-baseline-correlation-absent NEGATIVE
# preflight (scope:"precondition") GENERALIZES to a non-test artifact. They are the same idea
# at two altitudes: this one is the per-task TDD-red gate on the touched test SUBSET; the
# preflight is the pre-DAG, plan-wide absence baseline. scope:"local" (NOT integration) so this
# is never re-run at a union point after the code it tests is merged — the #165 union-inversion
# lesson, which is also why a negative PRECONDITION is never in the integration set (BLOCKER (d)).
$ErrorActionPreference = 'Stop'

# In a real plan: run ONLY the new tests and assert they FAIL on current (un-implemented) code.
#   dotnet test Acme.Payments.Core.Tests --filter Category=RequestId  # expect non-zero
#   if ($LASTEXITCODE -eq 0) { Write-Output 'new tests pass on un-implemented code — tautological'; exit 1 }
# SIMULATED here as a fixed pass (the tests are red on current code, as required).
Write-Output "new RequestId tests are red on current code (simulated)"
exit 0
