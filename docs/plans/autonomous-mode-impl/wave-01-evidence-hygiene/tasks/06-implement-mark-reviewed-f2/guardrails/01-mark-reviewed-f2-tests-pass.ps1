# catches: an F2 implementation that deviates from spec (fabricates review-artifact on a bad report,
#          breaks the bare path, or a non-symmetric digest), OR a regression in the existing
#          mark-reviewed CLI behaviour. Runs the authored F2 tests AND the pre-existing ReviewMarkerCliTests.
#          Re-emits failure detail at the END so the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~MarkReviewedF2Tests|FullyQualifiedName~ReviewMarkerCliTests" --nologo 2>&1
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
    Write-Output "mark-reviewed F2 tests failing — the stamp-time hygiene checks are not implemented to spec (see details above)"
    exit 1
}
exit 0
