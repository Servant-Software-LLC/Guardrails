# catches: this plan modifies RunCommand.cs and adds a new observer alongside
#          OnTheFlyLogSiteObserver in Guardrails.Cli (already covered by existing
#          Guardrails.Integration.Tests) - without this baseline, a work task's
#          tests-pass guardrail failure could be pre-existing breakage misattributed
#          to the task.
$ws = $env:GUARDRAILS_WORKSPACE
$proj = Join-Path $ws "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj"

$output = & dotnet test $proj -c Release --nologo 2>&1
$exitCode = $LASTEXITCODE
$fullLog = $output -join "`n"
Write-Output $fullLog

if ($exitCode -ne 0) {
    Write-Output "---"
    Write-Output "Guardrails.Integration.Tests is already failing on the starting code (before this plan's changes)."
    $failureLines = $output | Select-String -Pattern "Error Message:|Assert\.|Exception|Expected:|Actual:|\[FAIL\]"
    foreach ($line in $failureLines) {
        Write-Output $line.Line
    }
    Write-Output "Fix the pre-existing breakage before this plan builds on it."
    exit 1
}

exit 0
