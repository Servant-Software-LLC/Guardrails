# catches: a gate change that does not compile, across the whole solution - because the single term this
#          stage adds to RunReport.AllSucceeded is read by the Cli's exit-code and summary rendering and
#          by both test projects (section 6.5 traces all seven consumers).
#
#          One error message is worth recognising on sight: a CS0122 on LivePlanEditWatch.IsEditorArtifact
#          means stage 5's promotion to internal did not land. The answer is to ESCALATE, never to inline
#          a second copy of the ignore list - section 6.2 requires the gate and the watch to share ONE
#          predicate "so a future addition cannot reach one and miss the other", and section 15.2 names
#          "skip the ignore list" as the pressure every other route points at.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). These are builds.
$failures = @()

foreach ($project in @('src/Guardrails.Core', 'src/Guardrails.Cli', 'tests/Guardrails.Core.Tests', 'tests/Guardrails.Integration.Tests')) {
    dotnet build $project --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $failures += "$project does not build."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output "Everything is built here because AllSucceeded is read from the Cli and from both test projects. A CS0122 on IsEditorArtifact means stage 5's promotion did not land - escalate rather than inlining a second copy of the ignore list, which is what section 6.2 forbids."
    exit 1
}
exit 0
