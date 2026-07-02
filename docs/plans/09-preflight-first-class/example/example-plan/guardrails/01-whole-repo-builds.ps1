# catches: a merged plan branch that no longer BUILDS. Each task's own guardrails only see that
# task's isolated segment worktree; only a whole-repo build on the final merged HEAD catches an
# integration-only break — e.g. two tasks that each build fine alone but conflict once merged.
$ErrorActionPreference = 'Stop'

# In a real plan this re-runs the whole-repo build on the merged/union tree, e.g.:
#   dotnet build Acme.Payments.sln --nologo
# and exits non-zero on failure. SIMULATED here as a fixed pass — this is an illustrative sample
# plan, not wired to a real repo.
Write-Output "whole-repo build: green on the merged HEAD (simulated)"
exit 0
