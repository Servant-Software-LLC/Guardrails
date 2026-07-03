# catches: a BROKEN STARTING POINT — either the touched library's existing tests are already red,
# or the touched service fails to build, before any work in this plan starts. Halting here on a
# red baseline avoids letting 04-implement-correlation burn its retry budget trying to make a
# gate green that was never green to begin with (design-of-record 09-preflight-first-class §3.3).
#
# POLARITY: positive — exit 0 when the existing checks PASS, exit 1 when they fail. Runs ONCE,
# plan-wide, before any task's first wave — the plan-level "Full Flight Check" counterpart to a
# task's own guardrails. Deterministic (a build + touched-area test run) — never a live network
# probe, per the advisory live-probe guidance.
$ErrorActionPreference = 'Stop'

# In a real plan this would run the touched-area's EXISTING tests plus confirm the touched
# service still builds, e.g.:
#   dotnet build Acme.Payments.sln --nologo
#   dotnet test Acme.Payments.Core.Tests --filter Category!=New --nologo
# and exit non-zero if either is red. SIMULATED here as a fixed pass — this is an illustrative
# sample plan, not wired to a real repo.
Write-Output "Acme.Payments.Core.Tests + Acme.Payments.Api build: already green (simulated)"
exit 0
