# catches: a JournalModel.cs edit or a test file that does not COMPILE - garbage, a stray brace, or a
#          real type error. Widening a record that the whole harness constructs is the specific
#          exposure here: a member added as REQUIRED rather than nullable, or a new nested record
#          declared inside another type instead of beside it, breaks every existing construction site
#          in AttemptJournaler/Scheduler/TelemetryIngest - files this task may NOT edit - so the
#          failure must be reported as a compile error at this rung rather than surfacing as seven
#          unbound behaviours in guardrail 02.
#
# Cheapest-first: this runs before the per-test census in 02, so a compile error is diagnosed as a
#          compile error and the retry agent is aimed at the declaration rather than at the tests.
#
# Builds the TEST project, which builds Guardrails.Core with it - one invocation covers both halves of
#          this collapsed pair.
#
# -v q is correct on a `dotnet build` and only there (#462): the #179 "never -v q" rule governs test
#          commands, whose Error Message/Expected/Actual block the flag deletes. Build errors are the
#          build's own stdout and survive it.
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
    Write-Output "tests/Guardrails.Core.Tests does not build against the widened JournalModel.cs. Every Phase-1 member is OPTIONAL (nullable) with an init-only setter - a 'required' member would break the existing AttemptRecord/TaskJournalEntry/JournalDocument construction sites in AttemptJournaler.cs, Scheduler.cs and TelemetryIngest.cs, none of which is in this task's writeScope. AttemptSegments and RunEnvironment are TOP-LEVEL public sealed records beside AttemptUsage, not nested types."
    exit 1
}

Write-Output "Core test project builds - the widened journal records and their tests compile."
exit 0
