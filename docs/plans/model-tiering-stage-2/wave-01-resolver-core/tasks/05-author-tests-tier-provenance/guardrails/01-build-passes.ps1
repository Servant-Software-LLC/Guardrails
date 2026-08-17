# catches: a test file that does not COMPILE. Without this, the sibling tests-fail-on-current-code
#          check would read a compile failure (non-zero exit) as TDD red and certify garbage (#155).
#          -v q is fine HERE: this is a BUILD, and #179's failure-detail re-emit concerns dotnet TEST
#          output (the Error Message / Expected / Actual block). Compiler errors survive -v q.
#          No inner double quotes in any Write-Output below: PowerShell does not accept \" as an
#          escape - it emits a literal backslash and TERMINATES the string, silently garbling the
#          retry feedback with exit 0 (measured, not assumed).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - the authored tests must COMPILE and fail, not fail to compile. Both the test file and the ActionDefinition TierOrigin declaration are in your write scope. If the error is a missing symbol OUTSIDE that scope, do not edit that file - emit a needsHuman fragment to the state-out path instead."
    exit 1
}
exit 0
