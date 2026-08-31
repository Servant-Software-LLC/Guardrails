# catches: code that does not compile. Cheapest-first, and it spans BOTH assemblies deliberately: this
#          task's change crosses the Core -> Cli boundary (RunReport.Observations declared in
#          Guardrails.Core, consumed by RunCommand in Guardrails.Cli), which is the one seam a
#          single-project build would miss and the terminal gate would find hours later.
#
#          The specific shape it catches: DecisionEntry.Headline is required, and an entry built from
#          plan section 5.4's field table alone does not compile (CS9035). That omission was a defect
#          in the design of record, not in your reading of it.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD; banned only on `dotnet test` (#462).
$failures = @()

dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "src/Guardrails.Core does not build. A CS9035 on a DecisionEntry initializer means the required Headline member was not supplied - section 5.4's field table omits it, but the record requires it. A CS0246 on LivePlanEditWatch means task 08 did not land: escalate with needsHuman rather than writing the watch here, since its file is outside your writeScope."
}

dotnet build src/Guardrails.Cli --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "src/Guardrails.Cli does not build. This is the Core to Cli seam: RunCommand consumes RunReport.Observations, which must be declared in Guardrails.Core as a DEFAULTED init-only property so no existing consumer changes."
}

foreach ($p in @('tests/Guardrails.Core.Tests', 'tests/Guardrails.Integration.Tests')) {
    dotnet build $p --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $failures += "$p does not build against your change. The pins are outside your writeScope - fix the production code, not the tests."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
