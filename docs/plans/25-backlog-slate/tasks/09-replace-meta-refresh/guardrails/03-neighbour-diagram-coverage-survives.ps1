# catches: the cheapest wrong implementation of this task - DELETING the coverage instead of changing
#          the renderer. THREE existing test files are inside this task's write scope, for the single
#          purpose of retiring the ONE assertion the change makes false, and each sits directly beside
#          tests that stay true. An agent under guardrail pressure can turn every check on this task
#          green by removing neighbouring tests, and nothing else in the plan would see it: guardrail
#          02 only runs this task's OWN new class, and a suite whose tests were deleted PASSES.
#
# This is the per-test census (#375) run in its GREEN polarity - the mirror the catalogue names: the
# same TRX loop with `-ne 'Passed'` instead of `-ne 'Failed'`. It is a BEHAVIOURAL check (rung 1 of
# the #468 demotion gate), not a regex over a test file: it proves each named survivor actually RAN
# and actually PASSED on this task's tree. A deleted test is absent from the TRX and becomes its own
# named finding; a gutted test that still passes is caught instead by guardrail 02's own assertions
# plus /guardrails-review - state that residual rather than pretending this closes it.
#
# Two projects, deliberately (three suites across them): tests/Guardrails.Core.Tests and tests/Guardrails.Integration.Tests do
# not reference each other, so one `dotnet test` cannot see both. Each gets its own run and its own
# results directory; the findings accumulate into ONE list (#179) so a single attempt learns every gap.
#
# REGRESSION clauses, green on arrival BY DESIGN - the declared #478 exception ("this existing thing
# is still here and still passes" is green before the task by definition). Baselines MEASURED with
# Select-String over each exact subject, case-sensitive:
#   HtmlDiagramRendererTests.cs   Render_DuringRunFalse_HasNoMetaRefresh_AndInactiveSpinner    1
#   HtmlDiagramRendererTests.cs   Render_3ArgOverload_StillWorks_EmptyStatus_NoRefresh         1
#   OnTheFlyDiagramTests.cs       FinalStatic_SettlesStillRunningNodes_AsInterrupted_...       1
#   RunCommandFinalSiteSettleTests.cs  SettleAfterFault_SettlesBothPages_NoRefresh_NoFrozen... 1
#   RunCommandFinalSiteSettleTests.cs  SettleAfterFault_NeverThrows_EvenWhenJournalIsCorrupt. 1
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
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # this census reads the TRX (schema tokens, NOT localized), so the
                                     # check does not depend on it - kept so the logged summary is readable.

# THE MANIFEST: subject test class -> project, filter, and the survivor method names. Cross-checked BY
# HAND against tasks/09-replace-meta-refresh/action.prompt.md ("Do not touch anything else in those three
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
$i = 0
foreach ($suite in $suites) {
    $i++
    $resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-survivors-$PID-$i"
    Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

    # No -v q: pointless here (nothing is re-emitted) and it propagates onto forward checks by cloning (#462).
    $out = dotnet test $suite.Project --filter $suite.Filter --no-build --nologo `
           --logger 'trx;LogFileName=survivors.trx' --results-directory $resultsDir 2>&1
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

    # DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
    $xml      = [xml](Get-Content $trx.FullName -Raw)
    $recorded = @($xml.TestRun.Results.UnitTestResult)
    if ($recorded.Count -lt 1) {
        Write-Output "the TRX for $($suite.Project) records ZERO executed tests - the --filter '$($suite.Filter)' matched nothing, or every match is [Skip]ped. This is NOT a finding about the tests: do NOT delete or rewrite them."
        exit 1
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
    Write-Output "=== neighbour coverage census: $($failures.Count) survivor test(s) were deleted, skipped or broken by this task ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
