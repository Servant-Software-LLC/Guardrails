# catches: three different wrong implementations of the wave twin, only one of which stage 8's pins can
#          see on their own.
#
#          1. The wave WRITE still recomputes from disk - stage 8's P7a/P7b, red before this stage.
#          2. The disk-reading Compute(wave) was REPLACED rather than kept BESIDE the new pinned fold.
#             Section 5.4 is explicit that the READ form is unchanged and still reads current disk: the
#             wave-drift compare, the answer key and mark-reviewed all depend on it. The SHIPPED
#             WaveDefinitionHashTests drives that function directly, which is why it is in the filter.
#          3. THE ONE THE PLAN DOES NOT STATE, found by tracing what this stage does NOT touch. Section
#             15 row 9 changes the wave-completion WRITE only. The wave-drift COMPARE is a READ and stays
#             on disk - so on the next resume the harness compares a PINNED stamped value against a DISK
#             recompute. On an unedited tree those must be BYTE-IDENTICAL, or every completed wave reads
#             as drifted and the run halts under the default policy. Six shipped resume tests gate that,
#             and they are in the second suite below rather than left to the terminal gate. Note the two
#             wave-drift POSITIVE tests would still pass by accident, because any mismatch reads as
#             drift: a green on those is not evidence the fold is correct.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179).
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED (a German-culture box prints 'gesamt:' and no
# 'Total:'), which would invert the guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# THREE filters in ONE suite entry, parenthesised with BARE pipes. A backslash-escaped pipe is rejected
# by VSTest as an invalid condition and yields ZERO tests at exit 0 - a silent green (dotnet.md 4.3).
#   WaveExecutedDefinitionHashTests  - stage 8's pins, RED before this stage, green after.
#   WaveDefinitionHashTests          - the SHIPPED suite. It drives the disk-reading Compute(wave)
#                                      DIRECTLY, so it is the behavioural proof that section 5.4's
#                                      pinned form landed BESIDE it rather than replacing it. Without
#                                      this term, an implementation that swapped the read form for the
#                                      pinned one would pass everything else in this stage.
#   ExecutedDefinitionHashAnchorTests - stage 6's committed tripwire. It runs HERE so a ninth
#                                      TaskDefinitionHash.Compute call site introduced by this stage
#                                      fails at this stage, rather than at the terminal gate six
#                                      stages later on a task that cannot fix it.
# Every substring was checked for containment: 'WaveDefinitionHashTests' is NOT a substring of
# 'WaveExecutedDefinitionHashTests' (the 'Executed' segment sits between), so the two terms select
# disjoint classes.
#
# The SECOND suite entry is the six resume tests that section 5.4's byte-identity requirement gates -
# see the header. They are shipped, currently green, and must stay green.
$suites = @(
    @{ Project = 'tests/Guardrails.Core.Tests'
       Filter  = '(FullyQualifiedName~WaveExecutedDefinitionHashTests|FullyQualifiedName~WaveDefinitionHashTests|FullyQualifiedName~ExecutedDefinitionHashAnchorTests)'
       Hint    = 'If a WaveExecutedDefinitionHashTests pin failed, the wave WRITE is still recomputing - W5 is the wave-completion stamp in the Scheduler wave loop, and it must fold each task DefinitionHashAtLoad plus the WaveNode capture. If a WaveDefinitionHashTests test failed, the disk-reading Compute(wave) was REPLACED rather than kept beside the pinned form: section 5.4 keeps it unchanged for every READ - the wave-drift compare, the answer key and mark-reviewed. If the anchor test failed, this stage introduced a ninth TaskDefinitionHash.Compute call site.' }
    @{ Project = 'tests/Guardrails.Core.Tests'
       Filter  = 'FullyQualifiedName~SchedulerWaveExecutionTests'
       Hint    = 'These are the shipped wave RESUME tests, and they are the byte-identity gate section 5.4 implies but does not state. The wave-drift COMPARE still calls the disk-reading WaveDefinitionHash.Compute(wave); only the WRITE is pinned. If the pinned fold is not byte-identical to the disk fold on an unedited tree, every completed wave reads as DRIFTED on the very next resume and these tests halt. Reproduce the disk fold exactly - the per-task entries in wave-relative id order, then the wave gate folders, then brief.md, with the same labels and separators - rather than inventing a new framing.' }
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
Write-Output "Wave twin verified: the pinned fold is green, the shipped disk form still passes, the resume tests still pass, and no ninth call site appeared."
exit 0
