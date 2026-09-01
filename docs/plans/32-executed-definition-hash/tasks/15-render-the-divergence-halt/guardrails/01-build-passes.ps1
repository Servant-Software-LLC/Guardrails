# catches: a cross-project compilation error in the last code stage of the plan. RunCommand.cs sits at
#          the Core/Cli boundary and this stage touches five distinct surfaces in it - the halt render,
#          the exit-code branch, the terminal-gate not-evaluated fix, the delivery reason, and the
#          drift-accept refusal - so the whole solution is built rather than one project.
#
#          Guardrails.Cli carries NO InternalsVisibleTo into Guardrails.Core, which is why the members
#          this stage works with (RenderPlanEditWarning, DescribeDelivery) are public static. If a new
#          call needs something internal to Core, that is a Core change and Core is outside this task's
#          writeScope: escalate with needsHuman rather than widening either.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). These are builds.
$failures = @()

foreach ($project in @('Guardrails.sln')) {
    dotnet build $project --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $failures += "$project does not build."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output "The whole solution is built here because this is the last code stage and RunCommand sits at the Core/Cli boundary. If the error names a Core member, that member is outside this task's writeScope - escalate rather than reaching across."
    exit 1
}
exit 0
