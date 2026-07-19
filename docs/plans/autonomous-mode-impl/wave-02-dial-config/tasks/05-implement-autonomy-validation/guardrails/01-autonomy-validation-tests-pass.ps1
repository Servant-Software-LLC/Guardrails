# catches: a GR2039/GR2040 implementation that deviates from spec (misses the per-gate route-around,
#          over-fires on a valid config, or wrong severity), OR a regression in the existing validator.
#          Runs the authored AutonomyValidator tests AND the pre-existing PlanValidatorTests. Re-emits
#          failure detail at the END so the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AutonomyValidatorTests|FullyQualifiedName~PlanValidatorTests" --nologo 2>&1
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
    Write-Output "autonomy-validation tests failing — GR2039/GR2040 not implemented to spec (see details above)"
    exit 1
}
exit 0
