# catches: a route disclosure whose behaviour deviates from the two clauses this task owns, AND any
#          regression in the seven clauses tasks 07 and 08 made green. This task is LAST in the
#          TaskExecutor.cs chain, so it is the final place a chain-wide regression can be caught by
#          something that still HAS a retry budget - after this it is the wave exit gate and then the
#          plan terminal gate, where a failure withholds mergeOnSuccess delivery.
#
#          All nine wave-2 clauses must be green here, so the filter is the whole class - and, unlike
#          the per-task filters upstream, that is safe precisely because no clause is left owing to a
#          downstream task (wave 3's judge clause lives in a class this filter also matches only once
#          wave 3 authors it, and it is EXPECTED to be absent now, which is why the guard is a
#          minimum count rather than an equality).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'FullyQualifiedName~Stage2ConformanceTests'

# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455).
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the Stage 2 conformance suite is not fully green. If Reattempt_BoundByCostlyCeiling_... fails, check that the warning is emitted on attempt 2 and NAMES the block from TierResolution.CostlyCeilingBlocks; if a clause tasks 07/08 had green now fails, this change regressed the chain (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455), keyed to the wave's CLAUSE COUNT. Keyed on the EXECUTED count
# (Passed+Failed); "Total:" counts [Skip]ped tests, so a fully-[Skip]ped class would clear it.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 9) {
    Write-Output "exit 0 but only $ran conformance clause(s) executed - this guardrail certified less than it should. Wave 2 owes NINE named clauses; a lower count means one was renamed away from the names pinned in task 06's prompt, dropped, or is [Skip]ped. The plan terminal gate matches the SAME names and will fail there too."
    exit 1
}
exit 0
