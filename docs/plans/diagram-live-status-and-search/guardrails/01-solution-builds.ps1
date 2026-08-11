# catches: any task's change that compiles in isolation but breaks the whole-repo
#          build once every branch is merged onto the plan branch HEAD.
$ws = $env:GUARDRAILS_WORKSPACE
$sln = Join-Path $ws "Guardrails.sln"

$output = & dotnet build $sln -c Release --nologo 2>&1
$exitCode = $LASTEXITCODE
Write-Output ($output -join "`n")

if ($exitCode -ne 0) {
    Write-Output "---"
    Write-Output "Whole-repo build failed on the merged plan-branch HEAD."
    exit 1
}

exit 0
