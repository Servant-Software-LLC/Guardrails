# catches: an implementation that does not compile. Cheapest-first: it runs before the test guardrail so
#          a compile error is diagnosed as a compile error rather than as three failing behaviours. The
#          specific near-miss here is a HALF-WIDENED journaller: adding an optional segments parameter to
#          one AttemptJournaler method and not to the outcome methods that funnel through it, or widening
#          the method and not updating a TaskExecutor call site that passes positionally - on top of the
#          list task 12 widened immediately before this task ran. Both break at COMPILE time, not at
#          assertion time, and would otherwise be reported as test failures.
#
#          The Core TEST project is built rather than src/Guardrails.Core alone, deliberately: it builds
#          Guardrails.Core with it AND binds the authored tests against the edited executor. Every type
#          this task edits (GuardrailRunner, GuardrailRunResult, TaskExecutor, AttemptJournaler) is
#          `internal`, and Guardrails.Cli has NO InternalsVisibleTo into Core, so no CLI-side break is
#          reachable from this task's writeScope - a solution build here would cost time for no
#          additional coverage. The plan-level guardrails build the solution.
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
    Write-Output "GuardrailRunner.cs / TaskExecutor.cs / AttemptJournaler.cs do not compile. If you widened a journaller method's parameter list, widen it as an OPTIONAL trailing parameter defaulting to null (the way usage: and task 12's turn count already did) and update every call site. Do NOT edit ActionRunner.cs or Scheduler.cs to make something bind: both are outside this task's writeScope - the action clock is folded onto the returned ActionRun from TaskExecutor with a `with` expression, and ActionMs is already declared."
    exit 1
}

Write-Output "Core test project builds against the segmented attempt durations."
exit 0
