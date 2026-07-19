# catches: a wave-02 dial deliverable that regressed once all branches merged — the autonomy-block
#          parse / inert-by-default back-compat, the GR2039/GR2040 validation matrix, or the
#          --autonomous/--dial CLI failing on the merged HEAD. Terminal postcondition for the wave (LOCAL
#          — runs ONCE on the merged wave-02 HEAD). Scoped to wave-02's own test areas, NOT the whole
#          suite (wave-02 is an intermediate wave). Re-emits failure detail at the END (#179).
$failed = $false
$allOut = @()
$targets = @(
    @{ Proj = 'tests/Guardrails.Core.Tests';        Filter = 'FullyQualifiedName~AutonomyConfigTests|FullyQualifiedName~AutonomyValidatorTests' },
    @{ Proj = 'tests/Guardrails.Integration.Tests'; Filter = 'FullyQualifiedName~AutonomousModeCliTests' }
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
    Write-Output "wave-02 dial tests failing on the merged HEAD (autonomy config / GR2039-GR2040 / --autonomous CLI) — see details above"
    exit 1
}
exit 0
