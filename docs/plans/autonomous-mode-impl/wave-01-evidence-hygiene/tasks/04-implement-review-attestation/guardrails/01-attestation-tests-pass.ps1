# catches: an attestation implementation that deviates from spec, OR a regression in the existing
#          ReviewMarker behaviour. Runs the authored ReviewAttestation tests AND the pre-existing
#          ReviewMarkerTests (staleness/back-compat must survive). Re-emits failure detail at the END so
#          the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~ReviewAttestationTests|FullyQualifiedName~ReviewMarkerTests" --nologo 2>&1
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
    Write-Output "attestation tests failing — the ReviewMarker attestation block is not implemented to spec (see details above)"
    exit 1
}
exit 0
