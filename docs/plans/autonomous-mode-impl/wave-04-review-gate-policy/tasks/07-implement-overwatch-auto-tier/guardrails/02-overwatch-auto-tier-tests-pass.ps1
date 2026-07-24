# catches: an auto-tier gate that regressed the authored behavior — silently auto-applying without the
#          block present (the Option-(c) danger), FAILING to auto-apply with it, auto-applying a DENYLIST
#          op, or breaking the byte-identical back-compat (auto + no block must still prompt). Runs the
#          authored OverwatchAutoTierTests; re-emits failure detail at the END so the WHY reaches the retry
#          tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~OverwatchAutoTierTests" --nologo 2>&1
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
    Write-Output "overwatch auto-tier tests failing — the block-gated silent auto-apply / back-compat / denylist-floor behavior is not to spec (see details above)"
    exit 1
}
exit 0
