# catches: a re-baseline that does not COMPILE - most likely a broken reference after the rename this
#          stage performs (ARunCarryingOnlyAPlanEditObservation_FastForwardsAndExitsZero no longer
#          describes its behaviour once the run halts at exit 2 and does not deliver, so section 15.1
#          renames it).
#
#          A non-compiling test exits `dotnet test` non-zero IDENTICALLY to one that compiles and fails,
#          so without this the red census in guardrail 02 is gameable by garbage (#155).
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). These are builds.
$failures = @()

foreach ($project in @('tests/Guardrails.Integration.Tests')) {
    dotnet build $project --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $failures += "$project does not build."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output "Both rewrites are on values the file already holds - the CLI exit code, the delivery record, and the advisory string. Nothing new is needed. If a rename broke a reference, fix the reference; the method being renamed is ARunCarryingOnlyAPlanEditObservation_FastForwardsAndExitsZero, whose name no longer describes its behaviour."
    exit 1
}
exit 0
