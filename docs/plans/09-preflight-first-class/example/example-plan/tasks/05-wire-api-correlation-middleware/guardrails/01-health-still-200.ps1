# catches: the new correlation middleware BREAKING the existing /health route (a regression the
# plan-level positive preflight makes attributable — /health was proven up before this plan
# started, so a break here is provably THIS middleware's fault, not pre-existing).
#
# Note: unlike the plan-level positive preflight (which the advisory live-probe guidance forbids
# from starting a server — it is a deterministic byte-check), a live probe is acceptable HERE: in
# a TASK's own guardrail a flake costs only this task's retry budget, not the whole run. In a
# real plan this might spin up the API in-process and hit /health. SIMULATED here as a fixed
# pass.
$ErrorActionPreference = 'Stop'
Write-Output "GET /health still returns 200 after middleware wiring (simulated)"
exit 0
