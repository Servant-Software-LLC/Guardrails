# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. A non-compiling
#          "test" exits `dotnet test` non-zero IDENTICALLY to one that compiles and fails, so without
#          this the red census in guardrail 02 is gameable by garbage (#155). Stage 6 exists purely so
#          this can be green: with LivePlanEditWatch declared, these tests compile and fail
#          BEHAVIOURALLY.
#
#          A CS0122 on TaskDefinitionFiles means the `using Guardrails.Core.Journal;` is missing - it
#          is `internal` and lives in Journal, not Loading, and Guardrails.Core.csproj carries
#          InternalsVisibleTo for both test assemblies.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD; banned only on `dotnet test` (#462).
$failures = @()

dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "tests/Guardrails.Core.Tests does not build - LivePlanEditWatchTests.cs is not type-correct. If the error names LivePlanEditWatch, PlanEdit, PlanEditedFile or PlanEditKind, stage 6's stub does not declare the shape section 5.2 pins: escalate with needsHuman rather than editing src/**, which is outside your writeScope."
}

dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "tests/Guardrails.Integration.Tests does not build - PlanEditedDuringRunTests.cs is not type-correct. RunReport.Observations and the plan-edit/observed tokens are STAGE 9's deliverables (the wiring) and do not exist yet: assert on the decisions[] entry's boundary and decision STRINGS, not on a constant."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
