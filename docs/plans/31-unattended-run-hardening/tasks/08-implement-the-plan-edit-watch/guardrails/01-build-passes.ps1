# catches: code that does not compile. Cheapest-first, so a CS error surfaces with the compiler's own
#          message rather than as an opaque non-zero from a test run one guardrail later.
#          The specific shape for THIS task: a CS0122 on TaskDefinitionFiles means the
#          'using Guardrails.Core.Journal;' is missing - it is internal and lives in Journal, not
#          Loading, and Guardrails.Core.csproj carries InternalsVisibleTo for both test assemblies.
#
# Core and Core.Tests ONLY. Guardrails.Cli and the Integration test project consume nothing this task
# writes - LivePlanEditWatch has no consumer until task 09 wires it - so building them here would cost
# time and attribute task 09's future failures to this task.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (strips restore/banner chatter, keeps the compiler errors); banned only
# on `dotnet test`, where it deletes the failure detail (#462).
$failures = @()

dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "src/Guardrails.Core does not build. If the error names TaskDefinitionFiles, add 'using Guardrails.Core.Journal;' - it is internal and in Journal, not Loading. Do NOT reach for HashText.cs or TaskDefinitionFiles.cs: both are outside this task's writeScope, deliberately."
}

dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "tests/Guardrails.Core.Tests does not build against your change. The unit suite is task 07's deliverable and outside your writeScope - fix the production signature, not the tests. A shape change to PlanEdit / PlanEditedFile / PlanEditKind would break it, and those are pinned by plan section 5.2."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
