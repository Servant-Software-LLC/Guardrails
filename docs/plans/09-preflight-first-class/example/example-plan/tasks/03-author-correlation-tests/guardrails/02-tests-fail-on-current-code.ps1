# catches: a TAUTOLOGICAL test — one that passes even though the implementation does not exist
# yet. The new RequestId tests MUST be RED against the current code; a later green then proves
# 04-implement-correlation did the work, not a vacuous test.
#
# This is the per-task TDD-red gate (scope:"local") that the plan-level NEGATIVE preflight
# (`<plan>/preflights/02-correlation-id-absent.ps1`, also one-shot) generalizes to a non-test
# artifact. They are the same idea at two altitudes: this one is the per-task TDD-red gate on the
# touched test SUBSET; the plan-level preflight is the plan-wide, one-shot-at-run-start absence
# check. scope:"local" (NOT integration) so this is never re-run at a union point after the code
# it tests is merged — the same union-inversion lesson that keeps the plan-level negative
# preflight one-shot and out of the integration set.
$ErrorActionPreference = 'Stop'

# In a real plan: run ONLY the new tests and assert they FAIL on current (un-implemented) code.
#   dotnet test Acme.Payments.Core.Tests --filter Category=RequestId  # expect non-zero
#   if ($LASTEXITCODE -eq 0) { Write-Output 'new tests pass on un-implemented code — tautological'; exit 1 }
# SIMULATED here as a fixed pass (the tests are red on current code, as required).
Write-Output "new RequestId tests are red on current code (simulated)"
exit 0
