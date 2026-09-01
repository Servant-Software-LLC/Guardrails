# catches: a plan that merged twenty-four green segments into a solution that does not compile. Every
#          per-task build guardrail in this plan builds ONE project; none of them sees a cross-project
#          break, and this plan writes into BOTH production projects (Guardrails.Core and Guardrails.Cli)
#          plus both test projects. The specific break this gate is aimed at: task 03 adds six members to
#          JournalModel.cs that eight later tasks then reference by name, and a member renamed during a
#          retry would leave every one of those references dangling only once they are merged together.
#          It also catches the #175 shape - a CS0101 duplicate definition an AI-merge produced from two
#          siblings that both appended to a shared file - which 04-union-artifacts-sound deliberately does
#          NOT check for (see its header for why the duplicate-definition sub-check is omitted here).
#
# LOCAL by design (#165) - NO scope key in the sidecar. A whole-solution build is a TERMINAL
#          POSTCONDITION, not a union-safe invariant: at an intermediate union this plan's merged bytes
#          hold test files referencing members whose implementation task has not run yet (task 11's
#          AttemptEnvelopeTests names ActionRun.Turns before task 12 adds it), so a solution build there
#          FAILS and the harness rolls the wave back on a correct run. It belongs at the terminal gate,
#          on the fully merged HEAD, after every task has integrated - which is exactly where this runs.
#
# -v q is correct HERE and only here: this is a `dotnet build`, not a `dotnet test`. The #179 re-emit
#          rule and its "never -v q" clause govern test commands, whose Error Message/Expected/Actual
#          block the flag would delete. A build's error lines are its stdout and survive -v q.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$solution = 'Guardrails.sln'
if (-not (Test-Path $solution)) {
    Write-Output "PRECONDITION: $solution not found - this gate builds the whole solution and cannot run without it."
    exit 1
}

$log = & dotnet build $solution -c Release --nologo -v q 2>&1 | Out-String
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
    Write-Output "The merged plan branch does not build. Look first at src/Guardrails.Core/Journal/JournalModel.cs (task 03 owns every Phase-1 record member and most of the tasks downstream of it reference those members by name) and at the three files two tasks each write - AttemptJournaler.cs (06, 12, 12a, 16), TaskExecutor.cs (10, 12, 12a, 14) and TelemetryCommand.cs (22, 24) - which are serialized by dependsOn precisely so a blind duplicate definition should not be possible here."
    exit 1
}

Write-Output "Solution build green on the merged plan-branch HEAD."
exit 0
