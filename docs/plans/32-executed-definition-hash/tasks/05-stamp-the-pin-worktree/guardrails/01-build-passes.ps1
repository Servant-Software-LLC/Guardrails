# catches: a Scheduler edit that does not compile, and the specific cross-assembly failure this stage
#          can introduce. Section 15.2 hands this stage a second, smaller deliverable: promote
#          LivePlanEditWatch.IsEditorArtifact from private static to internal static, so stage 13's gate
#          and the watch can share ONE ignore predicate. Guardrails.Core.csproj grants InternalsVisibleTo
#          to both test assemblies, so both are built here - a widened accessibility that does not
#          actually widen shows up as a CS0122 in a test project, not in Core.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). These are builds.
$failures = @()

foreach ($project in @('src/Guardrails.Core', 'tests/Guardrails.Core.Tests', 'tests/Guardrails.Integration.Tests')) {
    dotnet build $project --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $failures += "$project does not build."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output "Both test projects are built here because this stage promotes LivePlanEditWatch.IsEditorArtifact from private to internal, and Guardrails.Core.csproj grants InternalsVisibleTo to both. A CS0122 anywhere means the promotion did not land; a CS0111 means a second declaration was added rather than the existing one widened."
    exit 1
}
exit 0
