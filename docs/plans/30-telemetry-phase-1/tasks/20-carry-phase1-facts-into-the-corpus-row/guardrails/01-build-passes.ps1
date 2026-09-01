# catches: a mapping that does not compile. Cheapest-first: it runs before the test guardrail so a
#          compile error is diagnosed as a compile error rather than as eight failing behaviours.
#
#          The near-miss here is the nullable chain. AttemptSegments is FLATTENED onto the row, so the
#          mapping reads attempt.Segments?.ActionMs - and writing attempt.Segments.ActionMs against a
#          nullable record binds as a warning-or-error depending on the tree's nullable settings, while
#          assigning a long? source to a long column does not bind at all. Both are type errors at the
#          initializer, not assertion failures, and reporting them as failing behaviours would send a
#          retry agent to the test file - which is outside this task's writeScope.
#
# -v q is correct on a `dotnet build` and only there (#462): the "never -v q" rule governs test commands,
#          whose Error Message/Expected/Actual block the flag deletes. Build errors are the build's own
#          stdout and survive it.
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
    Write-Output "The Core test project does not build. The thirteen columns were declared by 04a-extend-the-corpus-row-shape and the journal members by 03, so a type error here is in YOUR mapping: check that Segments is dereferenced as attempt.Segments?.ActionMs / ?.GuardrailMs (a null Segments must leave both columns null, not throw), and that each source type matches its column (Turns int?, ActionMs long?, GuardrailMs long?, RouteWarm bool?, CpuCount int?, TotalMemoryBytes long?, MaxParallelism int?). Do NOT edit src/Guardrails.Core/Telemetry/TelemetryRow.cs or the test file to make an error go away - both are outside this task's writeScope and would fail the write-scope check."
    exit 1
}

Write-Output "Core test project builds - the ETL mapping binds against the declared columns."
exit 0
