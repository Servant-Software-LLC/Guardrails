# catches: a RunOutcomeWiringTests file that does NOT compile. It references only SHIPPED symbols
#          (WhollyGreenButUndelivered / ReviewMarker / the CLI-run harness) + numeric literals, so it must
#          build against current code; a non-compiling "test" exits dotnet test non-zero identically to a
#          red one, masking a compile error the wiring tasks (whose writeScope excludes this file) could
#          never fix (#155).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build — the RunOutcomeWiringTests file is not type-correct against the shipped symbols"
    exit 1
}
exit 0
