# catches: a --autonomous/--dial implementation that deviates from spec (wrong default threshold, no loud
#          maxCostUsd warning, missing --dial validation, or an uncapped autonomous run), OR a regression
#          in the existing run CLI. Runs the authored autonomous-CLI tests AND the pre-existing
#          DryRunCliTests / ReviewMarkerCliTests. Re-emits failure detail at the END so the WHY reaches
#          the retry tail (#179).
$out = dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~AutonomousModeCliTests|FullyQualifiedName~DryRunCliTests|FullyQualifiedName~ReviewMarkerCliTests" --nologo 2>&1
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
    Write-Output "autonomous-CLI tests failing — --autonomous/--dial resolution or the required maxCostUsd warning is not implemented to spec (see details above)"
    exit 1
}
exit 0
