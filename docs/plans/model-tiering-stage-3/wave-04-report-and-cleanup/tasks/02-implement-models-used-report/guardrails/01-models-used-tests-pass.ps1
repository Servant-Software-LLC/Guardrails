# catches: the two ways this deliverable goes green while doing nothing an operator can see. (1) An
#          aggregation that counted the WRONG thing - a bucket for attempts that recorded no model, a zero
#          count, a dropped model, or a mismatch case that crashed on the absent `requestedModel` key. (2)
#          The aggregation being perfect and NOTHING CALLING IT: the unit half would pass over a helper no
#          run reaches, which is the #475 shape exactly (AttemptRecord.Usage shipped declared, read, and
#          assigned by no construction site, every guardrail green).
#
#          The second is why BOTH projects run here. ModelsUsedReportTests drives the real `run` command
#          end to end over a fake-claude plan and reads the model out of that run's own state/run.json, so
#          it is the one check in this wave that can tell a reached aggregation from an unreached one. A
#          Core-only guardrail would be blind to precisely the failure this line exists to avoid.
#          Re-emits the failure DETAIL at the END so the WHY reaches the retry tail (#179, dotnet.md 4.2).
#
# FILTER SCOPE (#455): each filter names exactly the ONE class of this task pair, in its own project, never
# the plan-wide `ModelTieringStage3` trait - which would also select waves 1-3's classes and assert the
# state of tests this task does not own. `ModelsUsedSummaryTests` and `ModelsUsedReportTests` are each a
# substring of no other test class in either project (verified 2026-08-23: neither `ModelsUsed` nor either
# full name occurs anywhere under tests/ on the entry tree).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the zero-match guard reads is LOCALIZED (#455)
$failures = @()

$groups = @(
    @('tests/Guardrails.Core.Tests', 'ModelsUsedSummaryTests',
      'the aggregation itself. A NotImplementedException here means the stub is still in place; a wrong count, a bucket for model-less attempts, or an empty-list-instead-of-null return are the three answers the tests distinguish'),
    @('tests/Guardrails.Integration.Tests', 'ModelsUsedReportTests',
      'the line reaching an operator. This one drives the REAL `run` command, so a failure here with the Core class green means the aggregation is right and the call site is wrong - look at RunCommand.PrintTotalCost before you look at JournalModelsUsed')
)

foreach ($g in $groups) {
    $proj = $g[0]
    $filter = "FullyQualifiedName~$($g[1])"
    # NO -v q on a TEST command: it suppresses the Error Message / Expected / Actual / Stack Trace block,
    # leaving the re-emit below nothing but test NAMES and defeating #179 by the flag alone.
    $out = dotnet test $proj --nologo --filter $filter 2>&1
    $testExit = $LASTEXITCODE                              # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }              # full log first

    # EXIT CODE FIRST, guard second (#455, FORWARD polarity): a test host that never ran exits NON-zero
    # with no summary at all, so checking the exit code first reports its real error instead of blaming
    # the filter.
    if ($testExit -ne 0) {
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 40                        # bound the block so it fits the ~60-line tail
        Write-Output ""
        Write-Output "=== $($g[1]) failure details (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
        $failures += "$($g[1]) is not green - $($g[2])"
        continue
    }

    # ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing, or a
    # malformed one, also exits 0. Key on the EXECUTED count (Passed+Failed); "Total:" would also count
    # [Skip]ped tests, so a fully-skipped class would pass it. Never on "No test matches ..." (that string
    # is verbosity-dependent and never appears here, the #248 failure).
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failures += "$proj exited 0 but executed ZERO tests - this guardrail certified nothing for that half. The filter '$filter' matched no tests or is malformed; $($g[1]) is authored by 01-author-tests-models-used-report and must be present"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== models-used tests: $($failures.Count) of $($groups.Count) class(es) not green ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output "Fix the implementation, never the tests - they are outside this task's writeScope and an edit to one fails the task immediately."
    exit 1
}
exit 0
