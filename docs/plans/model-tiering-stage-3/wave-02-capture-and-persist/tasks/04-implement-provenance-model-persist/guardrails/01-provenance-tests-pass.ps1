# catches: the two failures this task is actually exposed to.
#          (1) The datum reaching SERIAL runs only. The three Category=ObservedModelProvenance tests
#              assert run.json on both the serial journaller path and the deferred worktree settle - the
#              mode a real run takes - so a fold placed where only one path sees it reds here.
#          (2) Collateral damage to the shipped conformance suite. TaskExecutor.cs is central; a filter
#              narrowed to the three new methods would let a real regression through to the wave gate,
#              where no task's writeScope can fix it. So the WHOLE Stage2ConformanceTests class runs.
#              That is safe by construction rather than by hope: the fold is a no-op when the runner
#              reported no observed model, which is every pre-existing test in the class - and
#              ProvenanceModel_StaysTheResolvedRoute_WhenTheRunnerReportedNoModel pins exactly that. If a
#              pre-existing assertion breaks anyway, the prompt routes it to needs-human rather than to
#              an out-of-scope edit (#193: this task does not own that file, so it must not be trapped
#              into re-baselining it silently).
#          Deliberately NOT the whole Integration suite, and NOT the Core suite: task 01's
#          ObservedModelCaptureTests is RED until task 02 lands on a parallel branch, so a wider run
#          would manufacture a false red no work on THIS task could clear (#165/#176).
#          Re-emits the failure DETAIL at the END so the WHY reaches the retry tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the summary line the zero-match guard reads is LOCALIZED (#455)

# NO -v q on a TEST command (#179) - it suppresses the Error Message/Expected/Actual block entirely.
$out = dotnet test tests/Guardrails.Integration.Tests --nologo `
    --filter "FullyQualifiedName~Stage2ConformanceTests" 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# FORWARD polarity: the exit-code check comes FIRST, so a test host that never ran is reported as a
# failure rather than misdiagnosed as a bad filter (#455).
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "a failing ObservedModelProvenance test means the observed model is not reaching run.json on both record paths - check the fold is on the AttemptProvenance object (which rides PendingAttempt) and not on the attempt record. A failing PRE-EXISTING conformance test means collateral damage from the TaskExecutor.cs edit: do NOT edit that test file, it is outside this task's scope."
    exit 1
}

# ZERO-MATCH GUARD (#455): a --filter that selects nothing exits 0. Keyed on the EXECUTED count
# (Passed + Failed), never Total - Total counts [Skip]ped tests, so a fully-skipped selection would
# otherwise certify this task green.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the filter FullyQualifiedName~Stage2ConformanceTests selected nothing, so this guardrail certified nothing."
    exit 1
}

# The class run passing is necessary, not sufficient: a PASSING test is not named in default
# `dotnet test` output, so a green above cannot tell "this wave's five tests ran and passed" from
# "they were never authored, or vanished in a merge". Re-select them by trait and require all five to
# have EXECUTED - that is the silent half-failure this wave exists to catch, applied to itself.
$named = dotnet test tests/Guardrails.Integration.Tests --nologo --filter "Category=ObservedModelProvenance" 2>&1
$namedExit = $LASTEXITCODE
$namedRan = ([regex]::Matches(($named | Out-String), '(?:Passed|Failed):\s*(\d+)') |
             ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($namedExit -ne 0 -or $namedRan -lt 5) {
    $named | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "expected the 5 Category=ObservedModelProvenance tests (3 behavioural + 2 regression) to run and pass; $namedRan executed, exit $namedExit. A green from the wider class run with these missing would certify a suite that no longer contains this wave's proof."
    exit 1
}
exit 0
