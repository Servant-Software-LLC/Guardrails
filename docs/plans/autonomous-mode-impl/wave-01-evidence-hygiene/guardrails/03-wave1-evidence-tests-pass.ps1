# catches: a wave-01 evidence-hygiene deliverable that regressed once all branches merged — the
#          attestation round-trip / back-compat, mark-reviewed F2, or plan-hash behaviour failing on the
#          merged HEAD. Terminal postcondition for the wave (LOCAL — runs ONCE on the merged wave-01 HEAD,
#          not at every union). Scoped to wave-01's own test areas, NOT the whole suite (wave-01 is an
#          intermediate wave). Re-emits failure detail at the END so the WHY reaches the retry tail (#179).
$failed = $false
$allOut = @()
$targets = @(
    @{ Proj = 'tests/Guardrails.Core.Tests';        Filter = 'FullyQualifiedName~ReviewMarkerTests|FullyQualifiedName~ReviewAttestationTests' },
    @{ Proj = 'tests/Guardrails.Integration.Tests'; Filter = 'FullyQualifiedName~ReviewMarkerCliTests|FullyQualifiedName~MarkReviewedF2Tests|FullyQualifiedName~PlanHashCliTests' }
)
foreach ($t in $targets) {
    $out = dotnet test $t.Proj --filter $t.Filter --nologo 2>&1
    $out | ForEach-Object { Write-Output $_ }
    $allOut += $out
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}
if ($failed) {
    $detail = $allOut |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "wave-01 evidence-hygiene tests are failing on the merged HEAD (attestation / mark-reviewed F2 / plan-hash) — see details above"
    exit 1
}
exit 0
