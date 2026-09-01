# catches: a test file that does not COMPILE - garbage, a real type error, or an assertion reaching for
#          a member a LATER stage writes. A non-compiling test exits `dotnet test` non-zero IDENTICALLY
#          to one that compiles and fails, so without this the red census in guardrail 02 is gameable by
#          garbage (#155).
#
#          Two members this stage MAY name, because stage 3 already landed them: TaskNode.DefinitionHash
#          AtLoad and TaskNode.DefinitionFilesAtLoad. Everything milestone C introduces - the report
#          record, the journal field, the boundary token - belongs to stages 12, 13 and 15 and is a
#          CS0117 here.
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
    Write-Output "If the error names a member this plan writes LATER - ExecutedDefinitionDivergence, definitionHashAtSettle, a pinned wave fold - rewrite the assertion against what exists today. DefinitionHashAtLoad and DefinitionFilesAtLoad DO exist (stage 3 landed them), so those are fair game. src/** is outside this task's writeScope: never add the member to make the test compile."
    exit 1
}
exit 0
