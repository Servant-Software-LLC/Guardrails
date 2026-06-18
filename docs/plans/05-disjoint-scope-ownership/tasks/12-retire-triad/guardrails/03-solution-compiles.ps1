# catches: the triad removed from src/ but its consumers in the test projects still reference it -
#          01-build builds only src/Guardrails.Core and 02-triad-removed-from-source greps only src/,
#          so a captureHashes/restoreOnRetry/GR2013/GR2014 reference left in the Core OR the SEPARATE
#          Integration test assembly stays invisible until the terminal suite gate. Build the WHOLE
#          solution so the break is named AT task 12.
dotnet build Guardrails.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "Guardrails.sln does not compile after retiring the triad - a test/integration consumer still references captureHashes/restoreOnRetry/GR2013/GR2014; update PlanLoaderTests, PlanValidatorTests, StateFlowTests, StatePlanBuilder, ParallelRunTests."
    exit 1
}
exit 0
