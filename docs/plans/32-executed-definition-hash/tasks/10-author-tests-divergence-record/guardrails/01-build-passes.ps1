# catches: a test file that does not COMPILE, and the cause here is almost always the same one: reaching
#          for stage 12's journal field or stage 13's report record by NAME. Both are CS0117 on this
#          tree.
#
#          The way through is the one plan 31 used for the same shape: assert on the SERIALIZED artifact
#          rather than on a typed member - the run.json key set, and the decisions[] entries' boundary
#          and decision STRINGS. That is also what makes P10 a real full-list silence pin rather than a
#          check for one absent token, which plan 31 section 8 showed passes trivially when the mechanism
#          is broken.
#
#          A non-compiling test exits `dotnet test` non-zero IDENTICALLY to one that compiles and fails,
#          so without this the red census in guardrail 02 is gameable by garbage (#155).
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). These are builds.
$failures = @()

foreach ($project in @('tests/Guardrails.Core.Tests')) {
    dotnet build $project --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $failures += "$project does not build."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output "If the error names RunReport.ExecutedDefinitionDivergence, TaskJournalEntry.DefinitionHashAtSettle or a definition-divergence token constant, you have reached for stage 12's or stage 13's deliverable. Assert on the SERIALIZED artifact instead - the run.json key set and the decisions[] entries' boundary/decision STRINGS - which needs no new API and is what makes P10's full-list silence pin writable at all. src/** is outside this task's writeScope."
    exit 1
}
exit 0
