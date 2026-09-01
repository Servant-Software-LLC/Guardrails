# catches: two new properties on the most widely constructed model type in the repo breaking a compile
#          somewhere. This is the guardrail that carries section 15's claim that stage 3 legitimately
#          needs no tests/** path, so it deliberately builds BOTH test projects as well as Core:
#          tests/** holds 27 'new TaskNode' expressions across 21 files, and if any of them could not
#          absorb two more members the claim would be false and the right answer would be a plan change,
#          not a quiet widening of this task's writeScope.
#
#          The shape that keeps it true is pinned by guardrail 02: NULLABLE and NOT required. A required
#          member would break all 21 files at once, and CS9035 is what that failure looks like.
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
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output "If the error is CS9035 (required member not set) in a TEST file, the capture was declared 'required'. Section 5.2 decides against that deliberately - 27 hand-built nodes across 21 files would all have to change, inside a stage that may not write tests/** at all. Make both captures NULLABLE and non-required."
    Write-Output "If the error names TaskDefinitionFiles, the using Guardrails.Core.Journal is missing - it is internal and lives in Journal, not Loading."
    exit 1
}
exit 0
