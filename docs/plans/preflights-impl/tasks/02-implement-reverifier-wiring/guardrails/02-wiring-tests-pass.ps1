# catches: IReVerifier still null at MaxParallelism 1 - the phases would silently no-op in serial mode
#          (a hidden false-green). Drives the REAL SchedulerFactory via the composition-root wiring test.
#          Re-emits the failing assertion/exception at the END so the retry tail shows WHY, not just the
#          [FAIL] name (#179, dotnet.md §4.2).
$out = dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~ReVerifierWiring" --nologo 2>&1
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
    Write-Output "ReVerifier composition-root wiring test failing - SchedulerFactory does not wire IReVerifier at MaxParallelism 1 (see details above)"
    exit 1
}
exit 0
