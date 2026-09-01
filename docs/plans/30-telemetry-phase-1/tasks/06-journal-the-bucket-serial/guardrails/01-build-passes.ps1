# catches: an implementation that does not compile. Cheapest-first: it runs before the test guardrail
#          so a compile error is diagnosed as a compile error rather than as five failing behaviours.
#
#          The specific near-miss here is the explicit-interface arity forwarder documented at
#          RunJournal.cs:318-329. Adding a parameter to the public RecordSettle / RecordSettleWithAttempt
#          changes their arity, which stops them matching ISchedulerJournal's default-bodied members;
#          deleting or re-aritying a forwarder to "fix" a compile complaint makes every Scheduler call
#          dispatch to the interface's NO-OP default instead - a change that COMPILES CLEANLY and
#          silently stops the worktree settle journalling at all. This rung catches the honest half of
#          that mistake (it stops compiling); the test guardrail and task 16 carry the rest, and the
#          prompt names the trap explicitly.
#
# Builds the TEST project, which builds Guardrails.Core with it.
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
    Write-Output "RunJournal.cs / AttemptJournaler.cs do not compile. The bucket parameter is OPTIONAL and LAST ('string? bucket = null', after definitionHashAtSettle) so no existing call site changes. Do NOT change TaskFingerprintBucket.Classify's signature to make a call site fit - it takes exactly (IReadOnlyList<string>? writeScope, IReadOnlyList<GuardrailDefinition> guardrails) and a reflection test pins that. Do NOT delete or re-arity the explicit ISchedulerJournal forwarders at RunJournal.cs:318-329 and below RecordSettleWithAttempt: read their comment first."
    exit 1
}

Write-Output "Core test project builds against the widened recorders."
exit 0
