# scope: precondition  (SIMULATED — see the example/README.md and guardrails.json header)
#
# catches: a BROKEN STARTING POINT. The Acme.Payments.Core.Tests are the existing, green
# unit tests of the library this plan MODIFIES (it does not create it). If they are already
# red before we start, halt the whole run immediately with an honest "your starting point is
# broken" — rather than letting 04-implement-correlation burn its full retry budget trying to
# make a coverage gate green that was never green to begin with. This is the canonical #181
# POSITIVE unit-test baseline — the polarity that "fails fast on a broken start".
#
# POLARITY: positive — exit 0 when the existing tests PASS, exit 1 when they fail.
#
# Phase-2 first-class semantics (docs/plans/09-preflight-first-class.md):
#   - Runs ONCE in the pre-DAG preflight phase, against the integration worktree at the user's
#     HEAD, BEFORE any segment worktree is created. It is read-only (no fragment, no commit).
#   - It is NOT in any attempt lifecycle, NOT re-run at a union point, and NOT part of the
#     terminal integration gate (BLOCKER (d): a precondition is a one-shot, never re-run on
#     merged bytes).
#   - Volume-control gate (§"Volume-control gate"): the baselined library pre-exists (D1),
#     this plan MODIFIES it (D2, an authoring call), the check is the touched-area test SUBSET
#     (strictly narrower than the terminal whole-repo gate), and it is shared by ≥2 modifier
#     tasks (03 + 04), deduped to ONE baseline node.
$ErrorActionPreference = 'Stop'

# In a real plan this would run the EXISTING touched-area tests, e.g.:
#   dotnet test Acme.Payments.Core.Tests --filter Category!=New --nologo
# and exit non-zero if any pre-existing test is red. SIMULATED here as a fixed pass.
Write-Output "Acme.Payments.Core.Tests baseline: already green (simulated)"
exit 0
