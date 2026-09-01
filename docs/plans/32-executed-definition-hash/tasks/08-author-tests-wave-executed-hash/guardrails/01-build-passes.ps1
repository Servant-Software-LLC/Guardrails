# catches: a test file that does not COMPILE - and here that failure has a specific, likely cause worth
#          naming. The obvious way to write P7a and P7b is to reach for stage 9's not-yet-existing
#          WaveNode.DefinitionHashAtLoad or its pinned fold. That is a CS0117, and the fix is NOT to add
#          the member (src/** is outside this task's writeScope) but to assert on the JOURNAL's recorded
#          wave and task hashes, which exist today.
#
#          That constraint is also what protects the plan's own anti-echo-judge rule: a test that cannot
#          NAME the production pinned fold cannot compute its expectation with it (section 5.8).
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
    Write-Output "If the error names WaveNode.DefinitionHashAtLoad or a pinned wave-fold function, you have reached for STAGE 9's deliverable. Section 5.8 requires the expected fold to be reconstructed INDEPENDENTLY anyway - from the journal's recorded values and the HashText primitive - so the correct fix is the one the plan already asks for, not a widened writeScope. src/** is outside this task's writeScope."
    exit 1
}
exit 0
