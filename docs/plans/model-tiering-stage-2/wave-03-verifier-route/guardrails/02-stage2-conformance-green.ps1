# catches: a wave that merged green per-task but whose real-seam proof does not hold as a whole. The
#          five judge clauses are made green by the wiring (06) and must SURVIVE the carry (07),
#          which edits TaskExecutor and Scheduler - the files every task of every plan runs through.
#          This is the first and only place the WHOLE class runs at once after both, so a later task
#          that regressed an earlier one's clause is caught HERE, at the wave boundary, rather than
#          at the plan terminal gate where there is no retry budget and the blame lands on the wave.
#
#          It is also the wave-local mirror of the plan terminal gate's behaviour manifest: failing
#          here costs one wave gate, failing there withholds delivery after the whole plan has run.
#
# Deliberately does NOT credit GR2028 - it is a FILTERED run, and a filtered run cannot fail when a
# merge dropped something outside its filter. The sibling 01-wave-union-builds carries that.
# LOCAL - no scope key (#165): the full class is green only once the wiring has landed, which is
# false at every partial union inside this wave by construction.
# Re-emits the failure DETAIL at the END so the WHY reaches the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'FullyQualifiedName~Stage2ConformanceTests'
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
    Write-Output "the Stage 2 real-seam conformance suite is not green on the merged wave HEAD. If a WAVE-2 clause is failing, this wave regressed a shared execution path rather than extending it."
    exit 1
}

# ZERO-MATCH GUARD (#455), tightened to the CLAUSE COUNT: exit 0 alone does not mean the suite ran.
# Wave 2 owes nine facts and wave 3 adds five; a lower executed count means one was renamed, dropped
# or [Skip]ped - which would also silently break the plan terminal gate, since that gate matches the
# same names.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 14) {
    Write-Output "exit 0 but only $ran conformance test(s) executed - this wave gate certified less than it should. Wave 2 owes NINE named facts and wave 3 adds FIVE, so 14 is the floor. The plan terminal gate matches the SAME names, so it will fail there too."
    exit 1
}
exit 0
