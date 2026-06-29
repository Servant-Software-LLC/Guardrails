# Bucket A — shared positive baseline. A no-op-root doctrine TASK with an ordinary
# scope:"local" guardrail; this is doctrine that SHIPS and VALIDATES today (no schema or
# harness change). Its guardrail runs in the first wave like any task's; every modifier in the
# Acme.Payments.Core area `dependsOn` this baseline (deduped one-per-area).
#
# catches: a BROKEN STARTING POINT. The Acme.Payments.Core.Tests are the existing, green
# unit tests of the library this plan MODIFIES (it does not create it). If they are already
# red before we start, halt the run with an honest "your starting point is broken" — rather
# than letting 04-implement-correlation burn its full retry budget trying to make a coverage
# gate green that was never green to begin with. This is the canonical #181 POSITIVE unit-test
# baseline — the polarity that "fails fast on a broken start".
#
# POLARITY: positive — exit 0 when the existing tests PASS, exit 1 when they fail.
#
# Partition placement (docs/plans/09-preflight-first-class.md §"Bucket A"):
#   - Doctrine, fully expressible with existing primitives: a no-op-action ROOT task + a
#     normal guardrail + a `dependsOn` edge from every modifier. No `scope:"precondition"`,
#     no pre-DAG phase — those were WITHDRAWN.
#   - scope:"local": it is NEVER re-run at a union point and NOT part of the terminal
#     integration gate. A start-from-green baseline is true against the run's STARTING bytes;
#     re-running it at a later, post-merge `taskBase` would check a different claim.
#   - Volume-control gate (§"Volume-control gate"): the baselined library pre-exists (D1),
#     this plan MODIFIES it (D2, an authoring call), the check is the touched-area test SUBSET
#     (strictly narrower than the terminal whole-repo gate), and it is shared by >=2 modifier
#     tasks (03 + 04), deduped to ONE baseline node per area.
$ErrorActionPreference = 'Stop'

# In a real plan this would run the EXISTING touched-area tests, e.g.:
#   dotnet test Acme.Payments.Core.Tests --filter Category!=New --nologo
# and exit non-zero if any pre-existing test is red. SIMULATED here as a fixed pass.
Write-Output "Acme.Payments.Core.Tests baseline: already green (simulated)"
exit 0
