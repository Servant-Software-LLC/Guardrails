# catches: an implementation of LivePlanEditWatch that does not satisfy the section 5.2 contract. All
#          eight behaviours task 07 authored must now be green - Poll's empty/report/re-baseline
#          triple, Rebaseline's plan-wide and unknown-id semantics, the never-throws guarantee, and
#          the two SILENCE properties that decide whether this feature survives contact with an
#          operator: U7 (editor artifacts are ignored) and U8 (logs/ and state/ are outside the
#          definition surface). An advisory that fires on the harness's own writes stops being read
#          (#229), so those two are not cosmetic.
#
#          U7 in particular fails in BOTH directions, and only one of them is obvious: omit the ignore
#          list and the watch is noisy; "fix" it centrally in HashText instead and U7 passes while
#          every recorded definition hash moves - which turns the next resume of every affected plan
#          into a definition-drift halt. HashText.cs is outside this task's writeScope precisely so
#          the harness's own write-scope check catches that second route deterministically, before
#          this guardrail even runs.
#
#          Re-emits the assertion detail at the END so the WHY reaches the harness retry-feedback tail
#          (#179) - default `dotnet test` prints it mid-run and ends with only "[FAIL] <name>".
#
# SCOPE: the CORE class only. PlanEditedDuringRunTests is task 09's - it drives real runs through a
#        Scheduler that does not poll the watch until 09 wires it, so P1/P3/P5 are still RED here by
#        design. Selecting them would make this task unable to go green until a task that DEPENDS on
#        it had run - the #455 forward deadlock that validate and graph --check both pass.
$ErrorActionPreference = 'Continue'

# The run summary the zero-match guard reads is LOCALIZED - pin the culture FIRST (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = 'FullyQualifiedName~LivePlanEditWatchTests'

# NO -v q on a TEST command: it deletes the Error Message / Expected / Actual / Stack Trace block the
# re-emit below exists to surface, defeating #179 by the flag alone (#462).
$out = & dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                      # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }      # full log first, for the attempt's saved output

# EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero with
# no summary at all. Guard-first would swallow that and report "the filter matched ZERO tests - check
# the class name", a confident misdiagnosis pointing at a file this task may not edit.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "LivePlanEditWatchTests is RED. Fix LivePlanEditWatch.cs - the tests are outside your writeScope and editing one fails the task immediately. If U7 fails, the ignore list is missing from the WATCH; do NOT move it into HashText, which is out of scope and would move every recorded definition hash."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does not mean tests passed - a --filter matching nothing, or a
# malformed one, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count
# [Skip]ped tests, so a fully-skipped class would clear a Total-keyed guard.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the --filter '$filter' matched nothing, is malformed, or every matched test is [Skip]ped. This guardrail certified nothing. The class is task 07's deliverable; if it is genuinely absent, escalate rather than writing it."
    exit 1
}
exit 0
