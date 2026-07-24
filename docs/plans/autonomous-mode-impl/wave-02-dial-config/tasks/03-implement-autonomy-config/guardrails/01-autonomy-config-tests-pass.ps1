# catches: an autonomy-block parse that deviates from spec (wrong defaults, block not mapped, or —
#          worst — the inert-by-default guarantee broken so an existing config's behaviour shifts).
#          Runs the authored AutonomyConfig tests AND the pre-existing config tests (back-compat must
#          survive). Re-emits failure detail at the END so the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AutonomyConfigTests|FullyQualifiedName~AutonomyPolicyConfigTests|FullyQualifiedName~LogsAndRunConfigTests" --nologo 2>&1
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
    Write-Output "autonomy-config tests failing — the autonomy block parse / inert-by-default guarantee is not implemented to spec (see details above)"
    exit 1
}
exit 0
