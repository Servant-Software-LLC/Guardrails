# catches: an implementation that does not compile. Cheapest-first: it runs before the test guardrail
#          so a compile error is diagnosed as a compile error rather than as nine failing behaviours.
#          The specific near-miss here is a signature drift - widening Classify to take a TaskNode, or
#          narrowing the return from string? to string - which breaks the authored tests at COMPILE
#          time, not at assertion time, and would otherwise be reported as a test failure.
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
    Write-Output "src/Guardrails.Core/Telemetry/TaskFingerprintBucket.cs does not compile against the authored tests. Do NOT change Classify's signature to make it compile - it takes exactly (IReadOnlyList<string>? writeScope, IReadOnlyList<GuardrailDefinition> guardrails) and returns string?, and a reflection test pins that."
    exit 1
}

Write-Output "Core test project builds against the implemented classifier."
exit 0
