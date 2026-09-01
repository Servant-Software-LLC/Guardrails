# catches: an implementation that does not compile. Cheapest-first: it runs before the test guardrail so
#          a compile error is diagnosed as a compile error rather than as four failing behaviours. The
#          specific near-miss here is an edit to a `with` expression against an immutable record - a
#          mistyped member name or a wrong nullability on the provenance fold breaks at COMPILE time, not
#          at assertion time, and would otherwise be reported as a test failure.
#
#          The Core TEST project is built rather than src/Guardrails.Core alone, deliberately: it builds
#          Guardrails.Core with it AND binds the authored tests against the edited executor. Both edited
#          types (ActionRun, TaskExecutor) are `internal`, and Guardrails.Cli has NO InternalsVisibleTo
#          into Core, so no CLI-side break is reachable from this task's writeScope - a solution build
#          here would cost time for no additional coverage. The plan-level guardrails build the solution.
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
    Write-Output "ActionRunner.cs / TaskExecutor.cs do not compile. Do NOT add a ModelDigest member to AttemptRecord to make something bind - the digest rides AttemptProvenance (JournalModel.cs, grep 'Placement is D32'), and a reflection test pins that it is NOT on the record."
    exit 1
}

Write-Output "Core test project builds against the folded digest."
exit 0
