# catches: the pre-DAG phase does not halt on a red preflight, does not record/skip the B1 marker, or
#          re-runs on resume (false-halt) - the PlanPreflightPhase tests fail. Re-emits the failing
#          assertion/exception at the END so the retry tail shows WHY (#179, §4.2).
$out = dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~PlanPreflightPhase" --nologo 2>&1
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
    Write-Output "pre-DAG plan-preflight phase tests failing - see details above"
    exit 1
}
exit 0
