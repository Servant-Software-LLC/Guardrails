# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. With the minimal
#          stub this task also writes, the Core test project must build; a non-compiling "test" exits
#          dotnet test non-zero IDENTICALLY to a failing one, so without this the red signal that
#          guardrail 02 reads would be gameable by garbage (#155). It also catches the specific
#          near-miss this task is exposed to: a stub whose Classify signature drifted (a TaskNode
#          parameter added, or the return type narrowed from string? to string) would not bind against
#          the tests the same task wrote, and the failure would read as a test problem rather than a
#          signature one.
#
# Cheapest-first: this runs before the per-test census in 02, so a compile error is diagnosed as a
#          compile error rather than being reported as nine unbound behaviours.
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
    Write-Output "tests/Guardrails.Core.Tests does not build. The tests and the stub must COMPILE and FAIL - not compiling is a mistake to fix, not the intended TDD red. Check that TaskFingerprintBucket.Classify has exactly the pinned signature (IReadOnlyList<string>? writeScope, IReadOnlyList<GuardrailDefinition> guardrails) returning string?, and that the tests construct real GuardrailDefinition instances rather than a double."
    exit 1
}

Write-Output "Core test project builds - the tests and the stub compile."
exit 0
