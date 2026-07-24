# catches: wave-01 building on a Guardrails.Integration.Tests CLI review area that is ALREADY red on the
#          starting code (#181 brownfield green-start). mark-reviewed / plan-hash tasks extend this area;
#          a pre-existing failure here would be misattributed to a later task. Scoped via --filter to the
#          CURRENTLY-GREEN existing review-CLI tests only (NEVER the whole project — the #165/#176
#          compile-coupling trap), evaluated once before the DAG against the starting repo.
$out = dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~ReviewMarkerCliTests" --nologo 2>&1
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
    Write-Output "the existing ReviewMarkerCliTests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it"
    exit 1
}
exit 0
