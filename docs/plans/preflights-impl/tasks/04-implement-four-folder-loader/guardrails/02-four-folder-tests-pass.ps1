# catches: the loader/validator does not correctly parse the four folders or emit GR2027+ / re-home GR2018
#          / retire GR2017 - the four-folder tests still fail. Re-emits the failing assertion/exception at
#          the END so the retry tail shows WHY, not just the [FAIL] name (#179, dotnet.md §4.2).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~FourFolder" --nologo 2>&1
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
    Write-Output "four-folder loader/validation tests failing - see details above"
    exit 1
}
exit 0
