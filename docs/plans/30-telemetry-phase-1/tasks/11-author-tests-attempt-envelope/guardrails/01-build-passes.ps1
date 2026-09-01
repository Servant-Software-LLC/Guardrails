# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. A non-compiling
#          "test" exits dotnet test non-zero IDENTICALLY to a failing one, so without this the red
#          signal guardrail 02 reads would be gameable by garbage (#155). The specific near-miss here:
#          these tests reference members declared by TWO upstream tasks (AttemptRecord.Turns and
#          AttemptRecord.Segments/AttemptSegments from 03; ActionRun.Turns, ActionRun.ActionMs and
#          GuardrailRunResult.GuardrailMs from 04). A file written against a member that never landed
#          fails at COMPILE time, and that must be diagnosed as an upstream shape gap rather than
#          reported as six unbound behaviours.
#
# Cheapest-first: this runs before the per-test census in 02, so a compile error is diagnosed as a
#          compile error rather than as six absent tests.
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
    Write-Output "tests/Guardrails.Core.Tests does not build. The tests must COMPILE and FAIL - not compiling is a mistake to fix, not the intended TDD red. Check that AttemptRecord.Turns / AttemptRecord.Segments (task 03) and ActionRun.Turns / ActionRun.ActionMs / GuardrailRunResult.GuardrailMs (task 04) all exist, and that BOTH classes (AttemptTurnsTests and AttemptSegmentsTests), the stub IPromptRunner and every fixture helper live INSIDE this task's one test file rather than in a shared helper outside the writeScope."
    exit 1
}

Write-Output "Core test project builds - the attempt-envelope tests compile."
exit 0
