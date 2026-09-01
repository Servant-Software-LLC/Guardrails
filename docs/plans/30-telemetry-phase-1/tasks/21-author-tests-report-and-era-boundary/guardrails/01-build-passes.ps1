# catches: a test file that does not COMPILE - garbage, a real syntax/type error, or the specific drift
#          this task is uniquely exposed to. Its sibling 19-author-tests-row-carries-phase1-facts is the
#          plan's one COMPILE-red pair; this one must NOT become a second. A test that reaches for a
#          not-yet-written constant (a TelemetryCommand.EraBoundary, a bucket column name on some type
#          the CLI does not expose) stops the Integration project compiling, and a non-compiling "test"
#          exits dotnet test non-zero IDENTICALLY to a failing one - so without this, the red signal
#          guardrail 02 reads would be gameable by garbage (#155) and by an accidental compile red that
#          nobody would notice was the wrong shape of red.
#
#          The instruction that avoids it is in the action prompt and is worth restating in the failure
#          text: assert on the report's rendered STDOUT and write the boundary date as a LITERAL.
#
# Cheapest-first: this runs before the per-test census in 02, so a compile error is diagnosed as a
#          compile error rather than being reported as five unbound behaviours.
#
# -v q is correct on a `dotnet build` and only there (#462): the "never -v q" rule governs test commands,
#          whose Error Message/Expected/Actual block the flag deletes. Build errors are the build's own
#          stdout and survive it.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this guardrail builds the Integration test project (which builds Guardrails.Core and Guardrails.Cli with it) and cannot run without it."
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
    Write-Output "tests/Guardrails.Integration.Tests does not build. These tests must COMPILE and FAIL AT RUNTIME - not compiling is a mistake to fix, not the intended TDD red, and it is the specific failure mode this pair was shaped to avoid. If the error names a member of TelemetryCommand that does not exist, that is the drift: the era boundary date, the bucket column and the digest-bearing fingerprint are things task 22 will PRINT. Write the date as the literal '2026-08-31' in the test and grep the report's stdout for it - do not reference a constant. TelemetryRow's Phase-1 columns DO exist (04a-extend-the-corpus-row-shape declared them) and are fine to construct with."
    exit 1
}

Write-Output "Integration test project builds - the report tests compile against today's CLI, as this pair requires."
exit 0
