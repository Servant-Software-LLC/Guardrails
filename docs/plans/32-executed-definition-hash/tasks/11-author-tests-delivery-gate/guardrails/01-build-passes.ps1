# catches: a test file that does not COMPILE. The likely cause is the same as stage 10's - reaching for
#          the not-yet-written report record or journal field - and the answer is the same: P9, P11 and
#          P13 are assertions about the CLI exit code, whether the user's branch moved, what is on the
#          plan branch, and what the journal file says. None of that needs a new API member, which is
#          precisely why these pins can be authored before their implementers.
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
    Write-Output "If the error names RunReport.ExecutedDefinitionDivergence or TaskJournalEntry.DefinitionHashAtSettle, you have reached for stage 12's or stage 13's deliverable. P9, P11 and P13 are assertions about DELIVERY, the EXIT CODE, the plan branch and the journal file - all observable without any new API. src/** is outside this task's writeScope."
    exit 1
}
exit 0
