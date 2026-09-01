# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. This pair authors
#          NO stub: both of its dependencies (02's TaskFingerprintBucket, 03's TaskJournalEntry.Bucket)
#          already landed, so the tests compile against today's tree and their red is a RUNTIME red. A
#          non-compiling "test" exits dotnet test non-zero IDENTICALLY to a failing one, so without
#          this the red signal guardrail 02 reads would be gameable by garbage (#155).
#
#          The specific near-miss here is a MISSING DEPENDENCY masquerading as a test problem: if
#          TaskJournalEntry.Bucket or TaskFingerprintBucket.Classify is absent (a dependency edge that
#          did not deliver what it promised), the failure is a compile error about a missing symbol,
#          and it must be reported as that rather than as five unbound behaviours.
#
# Cheapest-first: this runs before the per-test red census in 02.
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
    Write-Output "tests/Guardrails.Core.Tests does not build. The tests must COMPILE and FAIL AT RUNTIME - not compiling is a mistake to fix, not the intended TDD red. AttemptJournaler, ActionRun and GuardrailRunResult are internal sealed and Core.Tests has InternalsVisibleTo, so construct them directly. RunJournal's constructor is private: the only way in is RunJournal.LoadOrCreate(PlanDefinition). If the missing symbol is TaskJournalEntry.Bucket or TaskFingerprintBucket, that is a dependency that did not deliver - escalate with needsHuman kind blocked-work rather than declaring a local copy."
    exit 1
}

Write-Output "Core test project builds - the authored tests compile against the unwired journal."
exit 0
