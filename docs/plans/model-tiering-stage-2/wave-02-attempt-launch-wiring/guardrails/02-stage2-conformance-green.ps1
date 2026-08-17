# catches: a wave that merged green per-task but whose real-seam proof does not hold as a whole. The
#          nine conformance clauses are made green by THREE different tasks (07 wires resolution, 08
#          settles no-route, 09 emits the route disclosure + D28 warning), each guarded by a filter
#          naming only ITS OWN clauses. This is the first and only place the WHOLE class runs at once
#          - so a later task that regressed an earlier task's clause (all three edit TaskExecutor.cs
#          in a chain) is caught HERE, at the wave boundary, rather than at the plan terminal gate
#          where there is no retry budget and the blame lands on the wave instead of the task.
#
#          It is also the wave-local mirror of the plan terminal gate's PART 2 behaviour manifest:
#          failing here costs one wave gate, failing there withholds mergeOnSuccess delivery.
#
# Deliberately does NOT credit GR2028 - it is a FILTERED run, and a filtered run cannot fail when a
# merge dropped something outside its filter. The sibling 01-wave-union-builds carries that.
# LOCAL - no scope key (#165): the full suite is green only once all three implementing tasks have
# landed, which is false at every partial union inside this wave by construction.
# Re-emits the failure DETAIL at the END so the WHY reaches the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)

# The class name, not the plan-wide trait: later waves add more Category=TierResolution classes
# (wave 3 EXTENDS this very class, but also lands its own), and a trait-only filter would silently
# widen to include them the moment they land.
$filter = 'FullyQualifiedName~Stage2ConformanceTests'
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# and reporting that as "your filter matched nothing" would send the next attempt after the wrong bug.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the Stage 2 real-seam conformance suite is not green on the merged wave HEAD - the shipped attempt-launch behaviour does not match DoR section 6 (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455), tightened to the CLAUSE COUNT: exit 0 alone does not mean the suite ran.
# Nine [Fact]s are owed by this wave; a lower executed count means tests were renamed, dropped, or
# [Skip]ped - which would also silently break the plan terminal gate's behaviour manifest, since that
# gate matches the same names. Keyed on the EXECUTED count (Passed+Failed); "Total:" counts [Skip]ped.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 9) {
    Write-Output "exit 0 but only $ran conformance test(s) executed - this wave gate certified less than it should. Wave 2 owes NINE named Stage2ConformanceTests facts; a lower count means one was renamed, dropped or [Skip]ped. The plan terminal gate matches the SAME names, so it will fail there too."
    exit 1
}
exit 0
