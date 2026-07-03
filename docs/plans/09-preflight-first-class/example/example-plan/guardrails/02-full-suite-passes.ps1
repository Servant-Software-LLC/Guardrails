# catches: a merged plan branch whose FULL touched-area suite regresses. A per-task guardrail
# only proves that task's own change didn't break its own narrow slice; only a full-suite re-run
# on the merged HEAD proves no task's change broke ANOTHER task's slice.
$ErrorActionPreference = 'Stop'

# In a real plan this re-runs the full touched-area suite on the merged/union tree, e.g.:
#   dotnet test Acme.Payments.Core.Tests --nologo
# and exits non-zero on any failure. SIMULATED here as a fixed pass — this is an illustrative
# sample plan, not wired to a real repo.
Write-Output "full Acme.Payments.Core.Tests suite: green on the merged HEAD (simulated)"
exit 0
