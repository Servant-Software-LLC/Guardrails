# catches: a shared predicate that satisfies the NEW rows while breaking the SHIPPED interlock. Task 06
#          deliberately routed the shipped SuppressingDecision through a throwing predicate, so every
#          pre-existing row in these two classes went red on purpose. Guardrails 01 and 02 select only
#          the OverwatchSupply-traited rows this pair added, so neither of them can see whether the
#          shipped rows came back - and the whole-suite gate does not run until the terminal task, by
#          which point the cause is many merges away.
#
#          This is the never-weaker half: the SAME two classes, with NO trait conjunct, so the
#          pre-existing rows are selected too - including TheOperatorOverrideStillLiftsTheInterlock
#          (--merge-on-success must still deliver past a suppressing decision, #361/#597) and
#          SuppressesDelivery_False_WhenNoMachineDecisionRecorded (an ordinary run still delivers).
#
#          Deliberately NOT the plan-wide trait and NOT the whole suite: the trait alone would assert the
#          state of every test in the plan (#455), and the whole suite is a terminal postcondition that
#          would fail here on this plan's other in-flight TDD reds.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)

# Class terms only - the two classes this task's change can break. Bare '|' in the alternation: '\|' is
# VSTest's escape character and yields "Incorrect format for TestCaseFilter", which exits 0 with ZERO
# tests - a silent green. Both substrings match exactly one class each (measured).
$filter = '(FullyQualifiedName~RunOutcomePolicyTests|FullyQualifiedName~WaveScopedInterlockTests)'

# No -v q on a TEST command: it deletes the failure block the re-emit below exists to surface (#462).
$out  = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

# EXIT CODE FIRST on a forward check (#455).
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== FAILURE detail (re-emitted at the END for the ~60-line retry tail, #179) ==="
    $inBlock = $false
    $emitted = 0
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*Failed\s+\S') { $inBlock = $true }
        elseif ($inBlock -and $line -match '^\s*(Passed!|Failed!|Skipped!|Passed\s+\S+\s+\[|Test Run)') { $inBlock = $false }
        if ($inBlock) { Write-Output $line; $emitted++ }
    }
    if ($emitted -eq 0) {
        Write-Output "(no per-test failure block in the runner output - the cause is ABOVE and is most likely a BUILD error or a crashed test host, not an assertion)"
    }
    Write-Output ""
    Write-Output "A row that was green BEFORE this plan started is red now. The shared predicate must reproduce the shipped behaviour exactly for proceeded-best-guess and proceeded-unreviewed, and must leave the operator override (--merge-on-success) able to deliver past a suppressing decision. Do NOT edit these tests to match a new behaviour - the interlock is never-weaker."
    exit 1
}

# ZERO-MATCH GUARD (#455): key on the EXECUTED count (Passed + Failed); "Total:" would also count
# [Skip]ped tests, so a fully-skipped run would certify this on nothing.
$ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}

Write-Output "Both interlock test classes green on the merged behaviour: $ran test(s) executed, including the rows that predate this plan."
exit 0
