# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. With the minimal
#          stub this task also writes, the Core test project must build; a non-compiling "test" exits
#          dotnet test non-zero IDENTICALLY to a failing one, so without this the red signal that
#          guardrail 02 reads would be gameable by garbage (#155).
#
#          It also catches the two specific near-misses this task is exposed to. First, a stub declared
#          `internal` rather than `public`: 18-record-the-run-environment calls Probe from
#          src/Guardrails.Cli/Commands/RunCommand.cs, a DIFFERENT assembly, so an internal stub compiles
#          fine here and blocks the next task with an error that reads as the next task's fault. Second,
#          a member spelled differently from the RunEnvironment record 03-extend-the-journal-record-shape
#          declared - the tests bind those names at COMPILE time, so a drift surfaces here rather than as
#          four failing behaviours.
#
# Cheapest-first: this runs before the per-test census in 02, so a compile error is diagnosed as a
#          compile error rather than being reported as four unbound behaviours.
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
    Write-Output "tests/Guardrails.Core.Tests does not build. The tests and the stub must COMPILE and FAIL - not compiling is a mistake to fix, not the intended TDD red. Check that RunEnvironmentProbe is PUBLIC and static with exactly the pinned signature Probe(int maxParallelism, string? harnessVersion, string? skillVersion) returning RunEnvironment, and that every member name your tests read off the returned record is spelled as JournalModel.cs declares it - read the RunEnvironment record before changing a test."
    exit 1
}

Write-Output "Core test project builds - the tests and the run-environment stub compile."
exit 0
