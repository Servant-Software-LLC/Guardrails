# catches: an implementation that does not compile. Cheapest-first: it runs before the test guardrail and
#          before the source-shape check in 03, so a compile error is diagnosed as a compile error rather
#          than as four failing behaviours or as an absent initializer line.
#
#          The specific near-miss here is a grain mistake that fails at COMPILE time and would otherwise
#          be mystifying: Bucket is declared on TaskJournalEntry, NOT on AttemptRecord (it is a TASK-grain
#          fact, constant across a task's own retries), so `Bucket = pending.Bucket` written inside the
#          `new Journal.AttemptRecord { ... }` initializer is CS0117. The bucket travels through
#          RecordSettleWithAttempt's optional bucket parameter instead. The second near-miss is arity: two
#          of this file's collaborators were widened by 06-journal-the-bucket-serial,
#          12-record-the-turn-count and 12a-segment-the-attempt-durations before this task ran, so a call
#          written against a remembered signature will not bind.
#
#          Building the Core TEST project (not just Guardrails.Core) is deliberate: it builds the
#          production assembly with it AND the authored tests against it, so a member renamed out from
#          under a test is caught here rather than reported as a behaviour failure.
#
# -v q is correct on a `dotnet build` and only there (#462).
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this guardrail builds the Core test project (which builds Guardrails.Core with it) and cannot run without it."
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
    Write-Output "AttemptJournaler.cs and/or Scheduler.cs do not compile. If the error names Bucket on AttemptRecord: Bucket is a TASK-grain member declared on TaskJournalEntry, so it cannot be set in the new Journal.AttemptRecord { ... } initializer - pass pending.Bucket through RecordSettleWithAttempt's optional bucket parameter instead. If the error is an argument-count or argument-type mismatch: re-read the CURRENT signature of the collaborator, since 06-journal-the-bucket-serial, 12-record-the-turn-count and 12a-segment-the-attempt-durations all widened members in these files before this task ran."
    exit 1
}

Write-Output "Core test project builds against the worktree-settle carriers."
exit 0
