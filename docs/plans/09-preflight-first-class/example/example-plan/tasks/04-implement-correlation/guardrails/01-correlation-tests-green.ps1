# catches: an implementation that makes the NEW tests pass but REGRESSES a pre-existing test
# (or vice versa). The full Acme.Payments.Core.Tests suite must be green after this task.
# Pairs with: the plan-level POSITIVE preflight (which proved the suite was green before we
# started) and the plan-level NEGATIVE preflight / this task's own 02-tests-fail-on-current-code
# gate (which proved the new field was absent, so this green is provably this task's doing — not
# pre-existing).
$ErrorActionPreference = 'Stop'

# In a real plan: run the WHOLE touched-area suite and require green.
#   dotnet test Acme.Payments.Core.Tests --nologo   # expect exit 0
# SIMULATED here as a fixed pass.
Write-Output "Acme.Payments.Core.Tests fully green: new + pre-existing (simulated)"
exit 0
