# catches: the cheapest wrong implementation of this task - DELETING the coverage instead of changing
#          the renderer. THREE existing test files are inside this task's write scope, for the single
#          purpose of retiring the ONE assertion the change makes false, and each sits directly beside
#          tests that stay true. An agent under guardrail pressure can turn every check on this task
#          green by removing neighbouring tests, and nothing else in the plan would see it: guardrail
#          02 only runs the DiagramRefreshTests class (which task 03 owns, not this task), and a
#          suite whose tests were deleted PASSES.
#
#          AND the second thing it catches, added 2026-08-29: a whole SUITE going red. The three
#          filters below select 48 + 10 + 2 = 60 tests, and the survivor loop reads the TRX outcome
#          of only FIVE of them, so a break in any of the other ~55 tests in the very files this task
#          may edit used to be invisible at task level - it would surface only at the plan-level
#          terminal gate, after the merge, attributed to nothing. The per-suite exit-code clause
#          closes that: the run has already happened, so it costs nothing.
#
# This is the per-test census (#375) run in its GREEN polarity - the mirror the catalogue names: the
# same TRX loop with `-ne 'Passed'` instead of `-ne 'Failed'`. It is a BEHAVIOURAL check (rung 1 of
# the #468 demotion gate), not a regex over a test file: it proves each named survivor actually RAN
# and actually PASSED on this task's tree. A deleted test is absent from the TRX and becomes its own
# named finding; a gutted test that still passes is caught instead by guardrail 02's own assertions
# plus /guardrails-review - state that residual rather than pretending this closes it.
#
# Two projects, deliberately (three suites across them): tests/Guardrails.Core.Tests and
# tests/Guardrails.Integration.Tests do not reference each other, so one `dotnet test` cannot see
# both. Each gets its own run and its own results directory; the findings accumulate into ONE list
# (#179) so a single attempt learns every gap, and the assertion text from any red suite is
# re-emitted at the very END of stdout so it survives the harness's ~60-line feedback tail.
#
# REGRESSION clauses, green on arrival BY DESIGN - the declared #478 exception ("this existing thing
# is still here and still passes" is green before the task by definition). Baselines MEASURED with
# Select-String over each exact subject, case-sensitive:
#   HtmlDiagramRendererTests.cs   Render_DuringRunFalse_HasNoMetaRefresh_AndInactiveSpinner    1
#   HtmlDiagramRendererTests.cs   Render_3ArgOverload_StillWorks_EmptyStatus_NoRefresh         1
#   OnTheFlyDiagramTests.cs       FinalStatic_SettlesStillRunningNodes_AsInterrupted_...       1
#   RunCommandFinalSiteSettleTests.cs  SettleAfterFault_SettlesBothPages_NoRefresh_NoFrozen... 1
#   RunCommandFinalSiteSettleTests.cs  SettleAfterFault_NeverThrows_EvenWhenJournalIsCorrupt. 1
# The per-suite EXIT-CODE clause is green on arrival for the same declared reason: all three suites
# pass on the tree this task is handed (MEASURED 2026-08-29 - 48 / 10 / 2 tests, exit 0 each), and
# they must still pass after. It is a regression clause, not a pre-satisfied requirement.
#
# NOT censused here, on purpose: Render_DuringRunTrue_InjectsMetaRefresh_AndActiveSpinner and
# DuringRun_Diagram_ShowsSpinnerThenSettledBadges_WithRefresh_ThenFinalHasNone are the two the action
# prompt tells this task to retire and rename. Demanding they survive would demand the task fail.
#
# RunCommandFinalSiteSettleTests is censused by its ORIGINAL name on purpose: unlike the other two
# subjects, this task edits it WITHOUT renaming it - only the during-run ReadDiagram() assertion is
# retired, and what the test proves is unchanged, so its name stays and this census can hold it.
#
# NAMED RESIDUAL, not a gap this closes: the sibling assertion on the very next line asserts the
# during-run INDEX still carries its meta refresh, and stripping it too would leave this census
# green. No clause here catches that. It is deliberately not chased with a source-shape grep
# (rung 3 for something rung 1 already holds): the index is rendered by LogSiteRenderer.cs, which is
# NOT in this task's write scope, and the behaviour is independently covered by
# OnTheFlyLogSiteTests.cs:33, which this task also cannot touch. So the worst reachable outcome is
# lost duplicate coverage in a file the terminal suite gate still runs - not an unverified behaviour.
# The exit-code clause does NOT close it either: deleting an assertion leaves its test GREEN, so the
# suite still exits 0. The clause catches a suite going RED, not a suite being quietly weakened.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # this census reads the TRX (schema tokens, NOT localized), so the
                                     # check does not depend on it - kept so the logged summary is readable.

# THE MANIFEST: subject test class -> project, filter, and the survivor method names. Cross-checked BY
# HAND against tasks/04-replace-meta-refresh/action.prompt.md ("Do not touch anything else in those three
# files") - the prompt<->manifest agreement is NOT mechanically enforced (measured on plan 24: validate
# exits 0 either way).
# NO class name here is a substring of any other *Tests class under tests/ - re-measured 2026-08-29
# after adding the third suite (nearest neighbours: ContainerDiagramTests, OnTheFlyLogSiteTests, and
# for RunCommandFinalSiteSettleTests exactly one class matches, itself).
$suites = @(
    @{
        Project  = 'tests/Guardrails.Core.Tests'
        Filter   = 'FullyQualifiedName~HtmlDiagramRendererTests'
        Survivors = [ordered]@{
            'the FINAL page still has no meta refresh and an inactive spinner' = 'Render_DuringRunFalse_HasNoMetaRefresh_AndInactiveSpinner'
            'the 3-arg plan-root overload still renders inert, badge-free'     = 'Render_3ArgOverload_StillWorks_EmptyStatus_NoRefresh'
        }
    },
    @{
        Project  = 'tests/Guardrails.Integration.Tests'
        Filter   = 'FullyQualifiedName~OnTheFlyDiagramTests'
        Survivors = [ordered]@{
            'a node still running at run end settles to interrupted, not a frozen spinner (#333)' = 'FinalStatic_SettlesStillRunningNodes_AsInterrupted_NotAFrozenSpinner'
        }
    },
    @{
        Project  = 'tests/Guardrails.Integration.Tests'
        Filter   = 'FullyQualifiedName~RunCommandFinalSiteSettleTests'
        Survivors = [ordered]@{
            'the fault-settle path still settles BOTH pages, with no refresh and no frozen spinner' = 'SettleAfterFault_SettlesBothPages_NoRefresh_NoFrozenSpinner'
            'settling still never throws on a corrupt journal and still settles the diagram'        = 'SettleAfterFault_NeverThrows_EvenWhenJournalIsCorrupt_AndStillSettlesTheDiagram'
        }
    }
)

$failures = @()
$detail   = @()   # #179: failure lines from any suite that exited non-zero, re-emitted at the END
$i = 0
foreach ($suite in $suites) {
    $i++
    $resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-survivors-$PID-$i"
    Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

    # NO -v q here, and now it is load-bearing rather than merely conventional: the suite exit-code
    # clause below re-emits the Error Message / Expected / Actual / Stack Trace block, and -v q
    # deletes exactly that block, leaving only "[FAIL] <name>" to re-emit - #179 defeated by the flag
    # alone (dotnet.md 4.3).
    $out = dotnet test $suite.Project --filter $suite.Filter --no-build --nologo `
           --logger 'trx;LogFileName=survivors.trx' --results-directory $resultsDir 2>&1
    $suiteExit = $LASTEXITCODE          # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }

    # PRECONDITION for this suite - the ONE legitimate early exit shape. No TRX means the run never
    # happened (host failed to start, wrong project path, malformed --filter which exits 0 SILENTLY).
    # Diagnose THAT: falling through would report every survivor deleted, a confident wrong message
    # aimed at test files this task IS allowed to edit - the worst possible misdirection here.
    $trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) {
        Write-Output "no .trx under $resultsDir for $($suite.Project) - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT delete or rewrite them."
        exit 1
    }

    # ZERO-RECORD GUARD, and the `| Where-Object { $_ }` is load-bearing - without it this check is
    # DEAD. MEASURED 2026-08-29: a --filter that matches nothing exits 0, prints "No test matches the
    # given testcase filter", and STILL WRITES A TRX carrying no <Results> element at all. On such a
    # TRX `@($xml.TestRun.Results.UnitTestResult)` is a ONE-element array holding $null, so `.Count`
    # is 1 and the un-filtered `-lt 1` test never fires: this census would fall through and report
    # every survivor DELETED - a confident wrong message aimed at test files this task IS allowed to
    # edit, which is the worst possible misdirection here. Filtering out the $null makes Count 0 and
    # the guard fire. All three cases measured: <Results/> empty -> old 1 / new 0; no <Results>
    # element -> old 1 / new 0; one real record -> old 1 / new 1 (unchanged on the healthy path).
    $xml      = [xml](Get-Content $trx.FullName -Raw)   # DOTTED navigation - the TRX has a default
                                                        # xmlns, so SelectNodes('//UnitTestResult')
                                                        # finds nothing.
    $recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($recorded.Count -lt 1) {
        Write-Output "the TRX for $($suite.Project) records ZERO executed tests - the --filter '$($suite.Filter)' matched nothing (dotnet test exits 0 SILENTLY in that case and still writes this empty TRX), or every match is [Skip]ped. This is NOT a finding about the tests: do NOT delete or rewrite them."
        exit 1
    }

    # SUITE EXIT CODE - the clause that closes the gap between "the five named survivors passed" and
    # "this suite is green". The three filters select 48 + 10 + 2 = 60 tests, and the survivor loop
    # below reads the TRX outcome of only FIVE of them. Without this, a break in any of the other ~55
    # tests in the very files this task is permitted to edit is INVISIBLE at task level - it would
    # surface only at the plan-level terminal gate, after the merge, attributed to nothing in
    # particular. The run has already happened by this line, so the check costs nothing.
    #
    # ORDER IS DELIBERATE and is NOT dotnet.md 4.3's "exit code first". That rule exists so a test
    # host that never started is not misreported as a bad filter; here the TRX precondition above
    # already makes that distinction (no TRX = the run did not happen), so by this point a TRX exists
    # WITH records and a non-zero exit unambiguously means tests FAILED. Checking it here rather than
    # before the precondition is what keeps the two diagnoses separate.
    #
    # ACCUMULATES rather than exiting: a break in suite 1 must not hide a deleted survivor in suite 3.
    # The failure DETAIL is collected now and re-emitted at the very END of stdout (#179), because
    # this is a tests-PASS archetype and the harness feeds back only the tail.
    if ($suiteExit -ne 0) {
        $failures += "the $($suite.Project) suite selected by '$($suite.Filter)' FAILED as a whole (exit $suiteExit). At least one test outside the survivor list below is broken. This task edits files in that suite, so the break is most likely yours: fix HtmlDiagramRenderer.cs, or the assertion you retired took a neighbouring test with it. Detail is re-emitted at the end of this log."
        $detail += $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 20
    }

    foreach ($behaviour in $suite.Survivors.Keys) {
        $name = $suite.Survivors[$behaviour]
        # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
        # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
        $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
        $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
        if ($hits.Count -lt 1) {
            $failures += "$behaviour -> '$name' did not run in $($suite.Project). It existed and passed before this task; it has been DELETED or RENAMED. This task's only permitted edit to that file is retiring the one assertion the meta-refresh change makes false - restore it."
            continue
        }
        $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
        if ($notGreen.Count -gt 0) {
            $seen = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "$behaviour -> '$name' is $seen in $($suite.Project), not Passed. It passed before this task ran, so the renderer change broke it. Fix HtmlDiagramRenderer.cs, not the test. ('NotExecuted' = someone added [Fact(Skip=...)], which is deletion with extra steps.)"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== neighbour coverage census: $($failures.Count) finding(s) - a survivor was deleted, skipped or broken, or a whole suite went red ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    # #179: the assertion/exception text goes LAST so it survives the harness's ~60-line tail.
    if ($detail.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
        $detail | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    }
    exit 1
}
exit 0
