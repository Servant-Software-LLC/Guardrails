# catches: a "red" that is really a BROKEN test. A test file that does not COMPILE exits non-zero
#          identically to one that compiles and fails, so without this the census below would accept
#          garbage as TDD red - and task 03's writeScope (src/Guardrails.Core/Execution/) EXCLUDES the
#          test file, so it could not repair the compile error either. The run would dead-end at
#          needs-human (#155). This is also the guardrail that proves the STUBS are real: the tests
#          name EventDelivery, the two new ctor parameters and GuardrailFailureReason.MaxChars, and
#          none of them compiles unless this task actually added them. Cheapest check first - garbage
#          fails HERE, unambiguously, instead of being reported as an unbound behaviour.
# Note: the test project references Guardrails.Core, so building it builds the stubbed source too -
#          a CS0414 from an unused private field, or a still-private MaxChars, fails right here.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$log = & dotnet build tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Debug --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Compile errors (test project + the Guardrails.Core stubs it references) ==="
    $log -split "`r?`n" | Where-Object { $_ -match 'error [A-Z]{2}\d+' } | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "The test project does not compile. A TDD red must COMPILE and fail - a test that cannot build is a broken test, not a red one. If the error names EventDelivery, the onRow/includeDetail parameters or GuardrailFailureReason.MaxChars, the stub for it is missing or wrong; see 'The stubs' in the prompt."
    exit 1
}

Write-Output "Test project (and the Guardrails.Core stubs it references) compiles."
exit 0
