# catches: any regression across the whole suite after all the harness/skill/example work merges - every
#          new phase/loader/journal/renderer test AND every pre-existing test must pass on the merged HEAD.
#          LOCAL (no scope): a full suite is a terminal postcondition (would red-halt a correct intermediate
#          union where a downstream TDD task has not run yet, #165). Re-emits the failing assertion/exception
#          at the END so the retry tail shows WHY, not just the [FAIL] name (#179, §4.2).
$out = dotnet test Guardrails.sln -c Debug --nologo 2>&1
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
    Write-Output "full test suite has failures on the merged HEAD (see failure details above)"
    exit 1
}
exit 0
