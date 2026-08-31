# catches: code that does not compile. Cheapest-first, so a CS error surfaces with the compiler's own
#          message rather than as an opaque non-zero from a test run two guardrails later. The
#          specific shape for THIS task: a new file in Guardrails.Core plus a call site added to
#          PlanValidator.Validate - a signature or namespace mismatch between the two lands here.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD; banned only on `dotnet test` (#462).
$failures = @()

dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "src/Guardrails.Core does not build. Check that HandoffScopeCoverage.cs declares the namespace PlanValidator.cs calls it from, and that the new DiagnosticCodes constants are spelled like their neighbours."
}

dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "tests/Guardrails.Core.Tests does not build against your change. The pins are outside your writeScope - fix the production code, not the tests."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
