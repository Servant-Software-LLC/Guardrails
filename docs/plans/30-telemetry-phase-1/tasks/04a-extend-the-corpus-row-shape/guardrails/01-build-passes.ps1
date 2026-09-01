# catches: a row declaration or a test that does not COMPILE. Cheapest-first: it runs before the census
#          in 02 so a compile error is diagnosed as a compile error rather than as six unbound
#          behaviours.
#
#          On a COLLAPSED data-model pair this guardrail carries more than usual, because it is the
#          rung the missing red rung would have occupied. A property either exists - the test compiles
#          and passes - or it does not, and then the test does not compile AT ALL. So "does the column
#          exist" is answered here, at the binder, and guardrail 02 is free to be about whether each
#          declared column behaves on the wire.
#
#          The specific near-miss: a column declared with the wrong TYPE. `int Turns` instead of `int?`
#          compiles against a round-trip test perfectly (it serializes 0 and reads 0 back) and is caught
#          only by the reflection test in 02 - but `long ActionMs` written as `int` breaks the binder
#          here, and reporting that as a failing behaviour rather than a type error would send a retry
#          agent to the wrong file.
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
    Write-Output "tests/Guardrails.Core.Tests does not build. This is a COLLAPSED pair - the record declaration IS the implementation - so a compile error means the thirteen columns and the tests that name them disagree. Check the exact names and types the prompt pins (Bucket string?, ModelDigest string?, Turns int?, ActionMs long?, GuardrailMs long?, RouteWarm bool?, Host string?, Os string?, CpuCount int?, TotalMemoryBytes long?, MaxParallelism int?, HarnessVersion string?, SkillVersion string?), and that TelemetryCorpusStore.JsonOptions is reachable (it is internal to Guardrails.Core and Guardrails.Core.Tests is in its InternalsVisibleTo set). Do NOT touch TelemetryIngest.cs or TelemetryCorpusStore.cs - both are outside this task's writeScope."
    exit 1
}

Write-Output "Core test project builds - the thirteen Phase-1 columns are declared and bind against their tests."
exit 0
