# scope: integration — the run's whole-repo soundness boundary, re-run on the merged bytes at
# every union point AND on the final merged HEAD (SSOT §3.3/§4.3).
#
# CONTRAST with a Bucket-B negative baseline (scope:"local", one-shot at run start): it asserts
# ABSENCE and is NEVER re-run at a union. An integration guardrail is the OPPOSITE — it is the
# set that IS re-run on every merged union. The two are deliberately disjoint (BLOCKER (d)):
# putting a negative/absence check in the integration set would false-fail post-merge, which is
# exactly why the negative baseline (02) is scope:"local" one-shot, not an integration guardrail.
$ErrorActionPreference = 'Stop'

# In a real plan: build the whole repo and run the touched-area suite on the merged bytes.
#   dotnet build  &&  dotnet test Acme.Payments.Core.Tests --nologo
# SIMULATED here as a fixed pass.
Write-Output "merged plan branch builds and Acme.Payments.Core.Tests are green (simulated)"
exit 0
