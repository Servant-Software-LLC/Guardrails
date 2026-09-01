# catches: a capture that MOVED an existing recorded hash. This plan's entire safety argument is section
#          5.5's no-op property - "on every run in which nobody edits the plan folder mid-run, this change
#          is a no-op down to the recorded bytes" - and everything downstream rests on it: no migration
#          wave, no plan resuming into a drift halt on upgrade, no re-staled review marker, and Part C's
#          safe-suffix rule 3 still resolving Safe for every legitimate modern settle.
#
#          Four shipped Core suites are where that claim is actually READ, and they are run here rather
#          than trusted: TaskDefinitionHashTests (the byte-level behaviour of Compute), WaveDefinitionHash
#          Tests (the fold), RunJournalDefinitionHashTests (what the journal records and omits) and
#          PlanDefinitionHashWaveTests. A stage-3 implementation that touched HashText, changed
#          TaskDefinitionFiles' enumeration, or altered the framing would turn one of them red HERE
#          rather than at the terminal gate thirteen stages later - and the answer would never be to edit
#          the suite. All four are outside this task's writeScope for exactly that reason.
#
#          It also carries the RECORD-EQUALITY question: TaskNode is a sealed record, so two new members
#          change Equals/GetHashCode. Nothing in src or tests compares two nodes today, which is why this
#          is a cheap re-run rather than a redesign - but "nothing compares them" is a fact about the
#          tree, and this is what re-checks it after the change lands.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail; default dotnet test prints them mid-run and ends with only [FAIL] name (#179).
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED - pin the culture BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# An explicit ALTERNATION of the four shipped class names, NOT the tempting 'FullyQualifiedName~
# DefinitionHash'. That substring is not discriminating: it also selects ExecutedDefinitionHashTests
# (stage 1's), which is legitimately RED until stage 4 - so the broad form would fail this stage for a
# reason it cannot fix, on a sibling's deliberately-failing tests (#193's orphaned-golden trap, arriving
# through a filter rather than a fixture). Each term below was checked for containment against every
# other class in the project and against every class this plan authors. Parenthesised alternation with a
# bare pipe: a backslash-escaped pipe is rejected by VSTest as an invalid condition and yields ZERO
# tests, exit 0 - a silent green (dotnet.md 4.3).
$filter = '(FullyQualifiedName~TaskDefinitionHashTests' +
          '|FullyQualifiedName~WaveDefinitionHashTests' +
          '|FullyQualifiedName~RunJournalDefinitionHashTests' +
          '|FullyQualifiedName~PlanDefinitionHashWaveTests)'

# NO -v q on a TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block, leaving
# only the [FAIL] line for the re-emit below to find - which defeats #179 by the flag alone (#462).
$out = & dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero with
# no summary at all, so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "a shipped definition-hash suite is red after stage 3. This plan changes WHEN the hash is computed and never WHAT it is computed over (sections 4.4, 5.5), so a red here means a recorded hash MOVED - which would owe a repo-wide drift wave the plan is explicitly designed to avoid. Fix the implementation; HashText.cs and TaskDefinitionFiles.cs are outside your writeScope and these four suites are not yours to edit."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed); 'Total:' would also count
# [Skip]ped tests, so a fully-skipped run would clear a Total-keyed guard.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The four-class alternation matched no tests, is malformed, or every match is [Skip]ped. It is the ONLY check on this plan's no-op property at this stage; fix the filter before proceeding."
    exit 1
}
Write-Output "Shipped definition-hash suites green: $ran tests executed, none failed - no recorded hash moved."
exit 0
