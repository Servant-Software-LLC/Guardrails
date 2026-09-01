# catches: a worktree settle that still recomputes from current disk, and - just as important - a fix
#          that went too far and pinned a READ site as well.
#
#          Stage 7's four pins are the behavioural half of this stage's verdict. P2 was RED before it
#          (stage 4 fixed only the serial sites) and must be green now: W2 is the write site the ISSUE
#          DOES NOT NAME and the one section 4.2 calls "the one that matters most", because plan 28's
#          motivating overnight run was a worktree-mode run. P3, P6a and P6b were green before and must
#          STAY green - P6a and P6b are the entire defence against the catastrophic wrong fix, which is
#          to pin the reads too and silence drift altogether.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail; default dotnet test prints them mid-run and ends with only the [FAIL] name (#179).
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED (a German-culture box prints 'gesamt:' and no
# 'Total:'), which would invert the guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# ONE suite, one filter. Discriminating (#455 companion (a)): nothing else in this project contains
# 'MidRunDefinitionEditTests'. This is the SAME string stage 7's inverse census uses - copied verbatim,
# never re-derived, so the two halves of the pair cannot drift apart.
#
# PlanEditedDuringRunTests is deliberately NOT in this filter. Stage 2 inverted two of its assertions
# and the second of them (a mid-run guardrail-script edit stops the run being wholly green) cannot go
# green until stage 13 builds the gate. Selecting it here would fail this stage for a reason it cannot
# fix - section 15's filtered-guardrail note, and the reason stages 3-12 all run filtered.
$suites = @(
    @{ Project = 'tests/Guardrails.Integration.Tests'
       Filter  = 'FullyQualifiedName~MidRunDefinitionEditTests'
       Hint    = 'If P2 failed, the WORKTREE settle is still recomputing from disk - W2 is Scheduler.SettleAsync (the deferred settle, the default for a real run) and W3 is SettleGreenIfWorktreeAsync; both stamp task.DefinitionHashAtLoad. If P6a or P6b failed, a READ site was pinned: section 11 says no task may do that, because pinning R1 makes P1 pass and silences definition drift entirely - a strictly worse product than today. The four Scheduler READ members (DetectDefinitionDrift, BuildResolvedTasks, ConsumePendingAnswers, ClassifyTaskGateAsync) keep calling TaskDefinitionHash.Compute. The test file is outside your writeScope: fix the implementation, never the assertion.' }
)

# ACCUMULATE (#478): one distinguishable message per suite, dumped once at the end.
$failures = @()

foreach ($suite in $suites) {
    # NO -v q on a TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
    # leaving only the [FAIL] line for the re-emit below to find - defeating #179 by the flag alone
    # (#462).
    $out = & dotnet test $suite.Project --filter $suite.Filter --nologo 2>&1
    $testExit = $LASTEXITCODE                              # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }

    # EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero
    # with no summary at all, so checking the exit code first reports its real error instead of blaming
    # the filter - a confident misdiagnosis pointing at the one artifact a retry agent may NOT edit here.
    if ($testExit -ne 0) {
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 40                        # bound the block so it fits the ~60-line tail
        Write-Output ""
        Write-Output "=== $($suite.Project) failure details (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
        $failures += "$($suite.Project) is red under filter '$($suite.Filter)'. $($suite.Hint)"
        continue
    }

    # ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
    # or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed); 'Total:' would also count
    # [Skip]ped tests, so a fully-skipped selection would clear a Total-keyed guard.
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failures += "$($suite.Project) exited 0 but executed ZERO tests under filter '$($suite.Filter)' - this guardrail certified nothing. The filter matched no tests, is malformed, or every match is [Skip]ped."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== $($failures.Count) suite(s) not green ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Worktree write sites verified: the mid-run definition-edit pins are green."
exit 0
