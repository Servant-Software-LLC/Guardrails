# catches: building on red - the EXISTING Core.Tests covering the model-resolution area this plan
#          modifies (ClaudeStreamParser, PromptExecutionSupport) are already failing on the starting
#          code, which would misattribute pre-existing breakage to this plan's tasks. Runs ONCE before
#          the DAG against the starting repo, --filter-scoped to the currently-green existing tests of
#          the touched area (NEVER the whole project - #165/#176 compile-coupling trap). #179 re-emit.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
Set-Location $ws
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~ClaudeStreamParserTests|FullyQualifiedName~PromptExecutionSupportModelTests" --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' | ForEach-Object { $_.Line } | Select-Object -First 40
    Write-Output ''
    Write-Output '=== Failure details (re-emitted for the halt feedback tail) ==='
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } } else { Write-Output '(no assertion lines matched - see full log above)' }
    Write-Output 'The area existing tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it.'
    exit 1
}
exit 0
