# catches: an implementation that does not compile. Cheapest-first: it runs before the test guardrail so
#          a compile error is diagnosed as a compile error rather than as nine failing behaviours.
#
# THE SOLUTION, not one project, and that is the whole reason this file differs from its siblings. This
#          task writes into THREE projects across the CLI seam - Guardrails.Core (the census),
#          Guardrails.Cli (the verb) and Guardrails.Integration.Tests (the wiring test) - and no
#          single-project build sees all three. The specific break: registering the leaf inside
#          TelemetryCommand.Create needs no CommandFactory change, so a mistaken edit that moves
#          registration OUT of Create and into a signature CommandFactory does not match would compile in
#          Guardrails.Cli alone and break only when the Integration project binds against it. Building
#          tests/Guardrails.Integration.Tests would in fact pull Cli transitively, but the solution build
#          is the honest statement of what this task touches and does not depend on a reference graph
#          staying the shape it is today.
#
# NO -c Release here, deliberately, and it is not an oversight: the plan-root gate builds Release, which
#          is right for a terminal postcondition, but a task-level build that pins a configuration
#          different from the one `dotnet test` uses two guardrails later would compile the tree twice
#          for one answer. Every per-task build guardrail in this plan uses the default configuration.
#
# -v q is correct on a `dotnet build` and only there (#462): the #179 "never -v q" rule governs test
#          commands, whose Error Message/Expected/Actual/Stack Trace block the flag deletes.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$solution = 'Guardrails.sln'
if (-not (Test-Path $solution)) {
    Write-Output "PRECONDITION: $solution not found - this guardrail builds the whole solution (this task spans Core, Cli and the Integration test project) and cannot run without it."
    exit 1
}

$log = & dotnet build $solution --nologo -v q 2>&1 | Out-String
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
    Write-Output "The solution does not build. Do NOT change TelemetryAttributionCensus.Census's signature or rename a member of AttributionCensusResult to make it compile - task 23's tests bind to both and are outside this task's writeScope. If the error is in TelemetryCommand.cs, note that task 22 edited this same file before you: locate the verb group by the text 'command.Add(BuildIngestLeaf(io));' rather than by a line number, and re-read the file as it is now."
    exit 1
}

Write-Output "Solution builds - the census, the telemetry census verb and the Integration test compile together."
exit 0
