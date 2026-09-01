# catches: a regression anywhere in Guardrails.Core.Tests, and the three claims this plan's section 5.5
#          no-op property is READ off:
#            TaskDefinitionHashTests   - the byte-level behaviour of Compute. This plan changes WHEN the
#                                        hash is computed and never WHAT it is computed over; if this
#                                        suite is red, a recorded hash MOVED, and that owes a repo-wide
#                                        drift wave the plan is explicitly designed to avoid.
#            WaveDefinitionHashTests   - the disk-reading wave fold. Section 5.4 keeps it UNCHANGED for
#                                        every read; a red here means the pinned form REPLACED it rather
#                                        than landing beside it, which also re-stales every wave review
#                                        marker keyed on it.
#            SchedulerWaveExecutionTests - the wave RESUME path, and it can go red for TWO UNRELATED
#                                        reasons. Say which before debugging, or the trail is cold:
#                                        (a) STAGE 9 - the wave-drift COMPARE still recomputes from disk
#                                            while the WRITE is now pinned, so the two must be
#                                            byte-identical on an unedited tree. If they are not, every
#                                            completed wave reads as drifted on the next resume, and the
#                                            failures cluster on the wave-drift tests as a GROUP;
#                                        (b) STAGE 13 + STAGE 17 - exactly TWO of its methods
#                                            (WaveDrift_CompletedWaveChanged_AutoPolicy_RewindsAndReRuns_
#                                            WithWaveBoundaryDecision and PendingFutureWaveEdit_IsNotDrift_
#                                            RunsNormally) modelled a resume as a second scheduler run over
#                                            the SAME in-memory plan, so run 2's nodes carried pins from
#                                            before the fixture's own on-disk edit and the settle-time gate
#                                            correctly reported a divergence. Stage 17 re-baselines those
#                                            two to load run 2's plan from disk, as a real resume does. If
#                                            ONLY those two are red, this is (b) and stage 17 did not land
#                                            or was reverted - NOT a fold mismatch.
#                                        The discriminator is the SET: (a) is a group of wave-drift
#                                        failures, (b) is exactly those two names and nothing else.
#          It also carries this plan's own two Core deliverables - ExecutedDefinitionHashAnchorTests (the
#          repo-lifetime call-site tripwire) and ExecutedDefinitionDivergenceTests (the silence and
#          provenance pins).
#
# LOCAL - no `scope` key (#165). A whole-suite run is a terminal postcondition: at an intermediate union it
#         selects a sibling task's intentionally-red TDD tests and red-halts a correct partial merge. It
#         runs once, here, on the fully-merged HEAD.
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED - pin the culture before the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the re-emit
# below exists to surface, defeating #179 by the flag alone (#462).
$out = dotnet test tests/Guardrails.Core.Tests --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero with no
# summary at all, so checking the exit code first reports its real error instead of blaming a filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the Core suite is red on the merged HEAD. If the failures are in TaskDefinitionHashTests or WaveDefinitionHashTests, a recorded hash MOVED - section 5.5's no-op property is broken and this plan now owes the repo-wide drift wave it was designed to avoid; restore the bytes rather than editing the suite. If they are in SchedulerWaveExecutionTests, read WHICH ones before assuming a cause - there are two, and they are unrelated. EXACTLY TWO methods red (WaveDrift_CompletedWaveChanged_AutoPolicy_RewindsAndReRuns_WithWaveBoundaryDecision and PendingFutureWaveEdit_IsNotDrift_RunsNormally) means stage 17's fixture re-baseline did not land or was reverted: those two modelled a resume as a second run over the SAME in-memory plan, so run 2 carried pins from before the fixture's own on-disk edit and the settle-time gate correctly reported a divergence. Fix the fixture, not the gate. A GROUP of wave-drift failures instead means the stage 9 pinned wave fold is not byte-identical to the disk fold, so every completed wave reads as drifted on resume."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does not mean tests passed - a run that executed nothing also
# exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count [Skip]ped tests, and
# tests/Guardrails.Core.Tests carries skipped tests today.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed in tests/Guardrails.Core.Tests - the terminal gate certified nothing. The test host did not run."
    exit 1
}
exit 0
