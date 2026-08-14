# catches: a plan that starts on RED - the existing Guardrails.Core tests already failing before any
#          task runs, so a work task's tests-pass guardrail fails from PRE-EXISTING breakage and the
#          retries are spent misattributing it. Filtered to the CURRENTLY-GREEN existing tests only:
#          a whole-project run would hit the #165/#176 compile-coupling trap once the TDD tasks land.
$ErrorActionPreference = 'Stop'
$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj `
    --filter "Category!=ModelTieringStage1" --nologo -v q 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Output $_ }
if ($code -ne 0) {
    Write-Output ""
    Write-Output "--- baseline failure detail ---"
    $log | Select-String -Pattern '^\s*(Failed|Error Message|Assert\.)' | ForEach-Object { Write-Output $_.Line }
    Write-Output "The area's existing tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it."
    exit 1
}
exit 0
