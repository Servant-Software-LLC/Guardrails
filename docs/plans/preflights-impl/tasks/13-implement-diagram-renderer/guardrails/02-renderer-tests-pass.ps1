# catches: the container-model goldens fail, OR an existing renderer/graph golden was left stale (not
#          updated to the new model). Runs the whole renderer/graph test surface (ContainerDiagram + the
#          existing Mermaid/Graph/Diagram tests). Re-emits the failing assertion/exception at the END so the
#          retry tail shows WHY (#179, §4.2).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~Diagram|FullyQualifiedName~Mermaid|FullyQualifiedName~Graph" --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "renderer/graph tests failing - the container model is wrong OR an existing golden diagram fixture was not updated (see details above)"
    exit 1
}
exit 0
