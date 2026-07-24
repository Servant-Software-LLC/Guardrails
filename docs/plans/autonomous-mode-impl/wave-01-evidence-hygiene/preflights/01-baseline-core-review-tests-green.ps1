# catches: wave-01 building on a Guardrails.Core.Tests review area that is ALREADY red on the starting
#          code (#181 brownfield green-start). If the existing ReviewMarker tests are broken before any
#          work runs, a later task's tests-pass failure would be misattributed to the task, not the
#          pre-existing breakage. Scoped via --filter to the CURRENTLY-GREEN existing review tests only
#          (NEVER the whole project — the #165/#176 compile-coupling trap), evaluated once before the DAG.
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~ReviewMarkerTests" --nologo 2>&1
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
    Write-Output "the existing ReviewMarkerTests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it"
    exit 1
}
exit 0
