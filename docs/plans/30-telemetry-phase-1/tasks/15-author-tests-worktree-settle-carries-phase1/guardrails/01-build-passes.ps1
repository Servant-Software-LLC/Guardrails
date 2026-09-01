# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. This task writes NO
#          stub: every member it names (PendingAttempt.Bucket/Turns/Segments, AttemptRecord.Turns/
#          Segments, TaskJournalEntry.Bucket) was declared by 03-extend-the-journal-record-shape and
#          04-extend-the-transport-record-shape, so the tests must bind against ALREADY-MERGED code. A
#          non-compiling "test" exits dotnet test non-zero IDENTICALLY to a failing one, so without this
#          the red signal that guardrail 02 reads would be gameable by garbage (#155).
#
#          The specific near-miss this task is exposed to: AttemptJournaler is `internal sealed` and these
#          tests call it directly through InternalsVisibleTo. If that attribute were missing, or the
#          entry-point signatures had moved under the three tasks that edit AttemptJournaler.cs first
#          (06-journal-the-bucket-serial, 12-record-the-turn-count, 12a-segment-the-attempt-durations),
#          the file would fail to bind - and that is a fact about the MERGED TREE, not about the tests.
#
# Cheapest-first: this runs before the per-test census in 02, so a compile error is diagnosed as a
#          compile error rather than being reported as four unbound behaviours.
#
# -v q is correct on a `dotnet build` and only there (#462): the #179 "never -v q" rule governs test
#          commands, whose Error Message/Expected/Actual block the flag deletes. Build errors are the
#          build's own stdout and survive it.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this guardrail builds the Core test project and cannot run without it."
    exit 1
}

$log = & dotnet build $project --nologo -v q 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Compilation errors (re-emitted so they land in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match 'error [A-Z]{2}\d+') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "tests/Guardrails.Core.Tests does not build. WorktreeSettlePhase1Tests must COMPILE and FAIL - not compiling is a mistake to fix, not the intended TDD red. Re-read the CURRENT signatures of AttemptJournaler.CompleteSucceededOrInvalidFragment and AttemptJournaler.ValidateFragmentForSettle (three tasks edit that file before this one), and check you spelled the Phase-1 members exactly as tasks 03 and 04 declared them - Bucket is on TaskJournalEntry, NOT on AttemptRecord."
    exit 1
}

Write-Output "Core test project builds - WorktreeSettlePhase1Tests compiles against the merged tree."
exit 0
