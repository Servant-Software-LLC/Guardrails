# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. This task writes
#          NO stub: the member it asserts on (AttemptProvenance.RouteWarm) was added by
#          03-extend-the-journal-record-shape, so the tests must bind against ALREADY-MERGED code. A
#          non-compiling "test" exits dotnet test non-zero IDENTICALLY to a failing one, so without this
#          the red signal that guardrail 02 reads would be gameable by garbage (#155).
#
#          The specific near-miss this task is exposed to: the three behavioural tests reach a PRIVATE
#          method (TaskExecutor.BuildProvenance) by reflection, and reflection binds at RUNTIME. A test
#          file can compile perfectly while every reflective lookup returns null - which is why guardrail
#          02 asserts each behaviour is observed Failed rather than merely present. This gate covers the
#          other half: the parts that DO bind at compile time (the AttemptProvenance member names, the
#          TaskExecutor constructor arity, TierResolution's initializer) really exist on the merged tree.
#
# Cheapest-first: this runs before the per-test census in 02, so a compile error is diagnosed as a
#          compile error rather than being reported as five unbound behaviours.
#
# -v q is correct on a `dotnet build` and only there (#462): the #179 "never -v q" rule governs test
#          commands, whose Error Message/Expected/Actual block the flag deletes. Build errors are the
#          build's own stdout and survive it.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this guardrail builds the Core test project and cannot run without it."
    exit 1
}

$log = & dotnet build $project --nologo -v q 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Compilation errors (re-emitted so they land in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match 'error [A-Z]{2}\d+') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "tests/Guardrails.Core.Tests does not build. RouteWarmthTests must COMPILE and FAIL - not compiling is a mistake to fix, not the intended TDD red. Check that you named AttemptProvenance.RouteWarm exactly as 03-extend-the-journal-record-shape declared it, that the TaskExecutor constructor call matches the shipped arity (see tests/Guardrails.Core.Tests/Journal/ExecutedDefinitionHashTests.cs for the form that compiles today), and that nothing in the file names TaskExecutor.BuildProvenance as a compile-time symbol - it is PRIVATE and reachable only by reflection."
    exit 1
}

Write-Output "Core test project builds - RouteWarmthTests compiles against the merged tree."
exit 0
