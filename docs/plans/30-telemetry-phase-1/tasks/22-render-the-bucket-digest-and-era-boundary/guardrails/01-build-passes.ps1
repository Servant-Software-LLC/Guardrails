# catches: an implementation that does not compile. Cheapest-first: it runs before the test guardrail so
#          a compile error is diagnosed as a compile error rather than as five failing behaviours.
#
# WHY THE SOLUTION AND NOT ONE PROJECT. This task's writeScope is a single Guardrails.Cli file, and the
#          test guardrail behind it builds Core + Cli + Integration.Tests on its way to running. What it
#          never builds is Guardrails.Core.Tests - so a change here that broke a Core-side signature this
#          file consumes would be invisible until the plan's terminal gate. Every other per-task build
#          guardrail in this plan can afford one project because it owns one project's compile surface;
#          this one sits at the seam between the two production assemblies and the two test assemblies,
#          which is exactly where the plan writes most.
#
# DEBUG, NOT RELEASE, and deliberately. The plan-root 01-solution-builds.ps1 gate builds -c Release at
#          the terminal union; a per-TASK guardrail must not, because a dogfood run launched from the
#          Release binary holds a lock on Core.dll and the Release build then fails for a reason that has
#          nothing to do with the task. Debug is what every other per-task build guardrail in this plan
#          uses, and a compile error does not depend on configuration.
#
# -v q is correct on a `dotnet build` and only there (#462): the "never -v q" rule governs test commands,
#          whose Error Message/Expected/Actual block the flag deletes. Build errors are the build's own
#          stdout and survive it.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$solution = 'Guardrails.sln'
if (-not (Test-Path $solution)) {
    Write-Output "PRECONDITION: $solution not found - this guardrail builds the whole solution and cannot run without it."
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
    Write-Output "The solution does not build. If the errors are in src/Guardrails.Cli/Commands/TelemetryCommand.cs they are yours: check that RenderLegend's signature still matches every call site if you widened it to carry an excluded-row count, and that the era-boundary constant is a DateTimeOffset compared against TelemetryRow.StartedAt rather than a string. If every error is in a file outside this task's writeScope, do NOT edit that file - write {`"needsHuman`": {`"question`": `"<the errors>`", `"kind`": `"blocked-work`"}} to the state-out path and stop."
    exit 1
}

Write-Output "Solution build green with the bucket, digest and era-boundary rendering in place."
exit 0
