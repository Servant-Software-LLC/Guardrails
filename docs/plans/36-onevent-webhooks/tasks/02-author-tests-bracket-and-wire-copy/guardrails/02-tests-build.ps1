# catches: a "red" that is really a BROKEN test. A test file that does not COMPILE exits non-zero
#          identically to one that compiles and fails, so without this the census in
#          03-tests-fail-on-stubs.ps1 would accept garbage as TDD red - and task 03's writeScope
#          (src/Guardrails.Core/Execution/) EXCLUDES the test file, so it could not repair the compile
#          error either. The run would dead-end at needs-human (#155). Garbage fails HERE,
#          unambiguously, instead of being reported as an unbound behaviour.
# WHAT THIS DOES *NOT* PROVE, corrected after a measurement (#468). This file used to claim it also
#          proved the STUBS were real - "the tests name EventDelivery, the two new ctor parameters and
#          GuardrailFailureReason.MaxChars, and none of them compiles unless this task actually added
#          them". That is transitive through content NOTHING ENFORCES, and it was MEASURED FALSE: a
#          RunEventBracketTests referencing none of the three compiles, and both of this task's
#          guardrails exited 0 over it. The prompt asks for those references; no check requires them.
#          The stub deliverables are asserted DIRECTLY and cheaply by 01-stubs-are-real.ps1, which has
#          already run and PASSED whenever this script executes at all under guardrailMode failFast -
#          so by the time anyone reads a failure here, EventDelivery and the internal MaxChars
#          provably EXIST and the fault is a genuine compile error, not a missing stub.
# What it DOES still prove about the source side: the test project references Guardrails.Core, so
#          building it builds the stubbed source too. A CS0414 from a stored-but-never-read private
#          field (the shape the prompt warns about, since this repo builds with TreatWarningsAsErrors)
#          and a non-defaulted new constructor parameter breaking the ~20 existing call sites both
#          fail right here. Those are compile facts; the stubs' EXISTENCE is guardrail 01's job.
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
    Write-Output "The test project does not compile. A TDD red must COMPILE and fail - a test that cannot build is a broken test, not a red one. Guardrail 01-stubs-are-real.ps1 ran BEFORE this one and PASSED, so the EventDelivery record and the internal MaxChars provably EXIST: do not go looking for a missing stub. The likely faults are the ones only a compiler sees - a CS0414 from an onRow/includeDetail stored in a private field that is never read (this repo builds with TreatWarningsAsErrors; discard them with '_ = onRow;' instead), a new constructor parameter that was not DEFAULTED and so broke the ~20 existing 'new RunEventStream(...)' call sites, or an ordinary error in the test file itself. See 'The stubs' in the prompt."
    exit 1
}

Write-Output "Test project (and the Guardrails.Core stubs it references) compiles."
exit 0
