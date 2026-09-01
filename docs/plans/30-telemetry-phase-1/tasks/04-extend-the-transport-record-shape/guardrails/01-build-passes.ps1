# catches: any of the four widened transport records - or the test file - failing to COMPILE. The
#          specific near-miss this task is exposed to is a member added as REQUIRED rather than
#          nullable: ActionRun, GuardrailRunResult, PromptResult and PendingAttempt are constructed at
#          many sites across TaskExecutor.cs, AttemptJournaler.cs, Scheduler.cs and the runner
#          quarantine - files this task may NOT edit - so a required member breaks all of them at once
#          and must be diagnosed here as a compile error rather than surfacing as five unbound
#          behaviours in guardrail 02. It also catches the dependency edge failing to deliver: this
#          task's PendingAttempt.Segments references Journal.AttemptSegments, which task 03 declares,
#          and its absence is a compile error rather than a test problem.
#
# Cheapest-first: this runs before the per-test census in 02.
#
# Builds the TEST project, which builds Guardrails.Core with it - one invocation covers all five files.
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
    Write-Output "tests/Guardrails.Core.Tests does not build against the widened transport records. Every new member is OPTIONAL (nullable, init-only, defaulting to null) - a 'required' member would break the existing construction sites in TaskExecutor.cs, AttemptJournaler.cs, Scheduler.cs and the prompt runners, none of which is in this task's writeScope. If the missing symbol is Journal.AttemptSegments, that type is task 03's deliverable: do NOT declare a local copy - escalate with needsHuman kind blocked-work."
    exit 1
}

Write-Output "Core test project builds - the widened transport records and their tests compile."
exit 0
