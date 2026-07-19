# catches: a plan-hash command that is unwired from the production dispatch, prints the wrong/non-hash
#          output, or is non-deterministic. The authored tests drive the REAL CommandFactory (not a
#          hand-added command), so this is the composition-root wiring proof (#120) — a command reachable
#          only from a hand-built root would not satisfy it. Re-emits failure detail at the END (#179).
$out = dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~PlanHashCliTests" --nologo 2>&1
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
    Write-Output "plan-hash tests failing — the command does not print the deterministic PlanDefinitionHash through the real dispatch (see details above)"
    exit 1
}
exit 0
