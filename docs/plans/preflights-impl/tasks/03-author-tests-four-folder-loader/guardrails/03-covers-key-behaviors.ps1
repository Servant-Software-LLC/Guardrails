# catches: a test file that satisfies build + tests-fail-on-stubs with ONE trivially-failing stub test
#          while skipping the enumerated four-folder scenarios. Lower-bound presence grep (a term in a
#          comment still matches - the residual is the tests-fail-on-stubs red + human review), scoped to
#          the files this task owns. One `if` per token so the failure names the missing scenario.
$codes = Get-Content "src/Guardrails.Core/Loading/DiagnosticCodes.cs" -Raw
if ($codes -notmatch 'GR2027') {
    Write-Output "DiagnosticCodes.cs does not allocate GR2027+ - the new four-folder diagnostic codes were not added"
    exit 1
}
$val = Get-Content "tests/Guardrails.Core.Tests/FourFolderValidationTests.cs" -Raw
if ($val -notmatch 'PlanGuardrails') {
    Write-Output "FourFolderValidationTests.cs does not reference PlanGuardrails - the re-homed GR2018 terminal-folder rule is untested"
    exit 1
}
if ($val -notmatch 'integrationGate') {
    Write-Output "FourFolderValidationTests.cs does not reference integrationGate - the GR2017/integrationGate retirement is untested"
    exit 1
}
$load = Get-Content "tests/Guardrails.Core.Tests/FourFolderLoaderTests.cs" -Raw
if ($load -notmatch 'PlanPreflights') {
    Write-Output "FourFolderLoaderTests.cs does not reference PlanPreflights - the plan-level preflights folder loading is untested"
    exit 1
}
exit 0
