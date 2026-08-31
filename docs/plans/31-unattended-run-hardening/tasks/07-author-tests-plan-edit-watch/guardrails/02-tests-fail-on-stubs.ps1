# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, any assertion that never invokes the subject). It
#          PASSES against stage 6's throwing stubs and hides behind its genuinely-failing siblings, so
#          a suite-level non-zero exit certifies the file honest while proving nothing (#375). One
#          entry per enumerated behaviour, each observed Failed in the runner's OWN TRX - never merely
#          discovered by name, which a hollow body satisfies exactly as a comment satisfies a token
#          floor.
#
#          This is the strongest anti-tautology check the plan-edit watch has, and stages 8 and 9 stake their
#          entire verdict on these tests being real. Stage 6 exists so that the red here is
#          BEHAVIOURAL: Poll() and Rebaseline() throw NotImplementedException, so a test that does not
#          fail never invoked them.
#
# DECLARED EXEMPTIONS - P2 and P4, and the reason is structural rather than convenient:
#   P2 AJitWaveBreakdownFollowedByRevert_EmitsZeroPlanEditEntries asserts ZERO plan-edit entries. With
#      the watch inert nothing emits one at all, so a CORRECT test is GREEN on the stub tree.
#      Demanding red would demand a correct implementation fail. Its job is to STAY green after stage
#      9 (the wiring): WaveBreakdownInvoker runs a Claude subprocess rooted at the plan directory with Write/Edit/
#      Bash at acceptEdits and no containment hook, so without the plan-wide re-baseline the watch
#      would report the harness's own writes as operator edits - and an advisory that fires on the
#      harness's own writes stops being read (#229).
#   P4 AStrayDsStoreMidRun_EmitsNothingWhileTheDefinitionHashStillChanges is the same shape: both its
#      halves are true today (nothing is emitted; the hash already counts a .DS_Store), and both must
#      stay true. It proves the watch is quieter than the hash BY DESIGN rather than by accident.
#   Both assert Expect='Executed' (present in the TRX, not [Skip]ped). They stay IN the manifest: a
#   dropped row and an oversight look identical from the outside.
#
#   Two of thirteen exempt is the honest ratio. If a later edit pushes it much past that, the red
#   census has become a forward one wearing its name - the signal to re-read the split, not to add
#   another exemption.
#
# WHAT THIS CENSUS CANNOT SEE: it proves each test is COUPLED to the code path (it fails when the
#          implementation is absent), never that its ASSERTION is correct. A test that calls Poll()
#          and then asserts something hollow is red on stubs, green after, and passes. P5 - the
#          rendered text carrying all three section 5.1 consequences - is the one where that residual
#          matters most, because a half-true message actively misleads a human; it stays a human read.
$ErrorActionPreference = 'Continue'

# The census reads TRX schema tokens (not localized); keep the pin so the log stays readable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$suites = @(
    @{
        Project  = 'tests/Guardrails.Core.Tests'
        Filter   = 'FullyQualifiedName~LivePlanEditWatchTests'
        Label    = 'Core'
        Manifest = [ordered]@{
            'U1 Poll with nothing changed returns empty'                     = 'Poll_WithNothingChanged_ReturnsEmpty'
            'U2 a modified guardrail script is reported with its task+file'  = 'Poll_AfterAGuardrailScriptIsModified_ReportsThatTaskAndThatFile'
            'U3 Poll re-baselines, so a second Poll is empty'                = 'Poll_ReBaselines_SoASecondPollAfterOneEditIsEmpty'
            'U4 Rebaseline() with no ids silences the whole plan'            = 'Rebaseline_WithNoIds_SilencesTheWholePlan'
            'U5 Rebaseline with an unknown task id is a no-op'               = 'Rebaseline_WithAnUnknownTaskId_IsANoOp'
            'U6 Poll never throws on an unreadable file'                     = 'Poll_WithAnUnreadableFile_DoesNotThrow'
            'U7 editor artifacts are ignored (the section 5.2 list)'         = 'Poll_IgnoresEditorArtifacts_DsStoreThumbsDbSwpOrigRej'
            'U8 logs/ and state/ are outside the definition surface'         = 'Poll_IgnoresLogsAndState_TheHarnessOwnWritesUnderThePlanFolder'
        }
    },
    @{
        Project  = 'tests/Guardrails.Integration.Tests'
        Filter   = 'FullyQualifiedName~PlanEditedDuringRunTests'
        Label    = 'Integration'
        Manifest = [ordered]@{
            'P1 a mid-run guardrail edit emits exactly ONE observed entry'   = 'AGuardrailEditedMidRun_EmitsExactlyOneObservedPlanEditDecision'
            'P2 a JIT wave breakdown + revert emits ZERO entries'            = @{ Name = 'AJitWaveBreakdownFollowedByRevert_EmitsZeroPlanEditEntries'; Expect = 'Executed' }
            'P3 an observation is outcome-INERT: fast-forwards, exits 0'     = 'ARunCarryingOnlyAPlanEditObservation_FastForwardsAndExitsZero'
            'P4 a stray .DS_Store is silent while the HASH still changes'    = @{ Name = 'AStrayDsStoreMidRun_EmitsNothingWhileTheDefinitionHashStillChanges'; Expect = 'Executed' }
            'P5 the rendered text carries all THREE section 5.1 consequences' = 'TheRenderedText_CarriesAllThreeSection51Consequences'
        }
    }
)

$failures = @()

foreach ($suite in $suites) {
    $label      = $suite.Label
    $resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr31-watch-census-$label-$PID"
    # --results-directory is NOT cleared between runs: a stale TRX from a previous attempt would be
    # read as THIS attempt's evidence.
    Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

    # No -v q: pointless here (nothing is re-emitted) and it propagates onto forward checks by
    # cloning a sibling file (#462).
    $out = & dotnet test $suite.Project --nologo --filter $suite.Filter `
           --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
    $out | ForEach-Object { Write-Output $_ }

    # PRECONDITION - the ONE legitimate early exit per suite. No TRX means the run never happened
    # (test host failed to start, wrong project path, or a MALFORMED --filter, which exits 0
    # SILENTLY). Diagnose THAT; falling through would print "every behaviour unbound", a confident
    # wrong message aimed at the one artifact a retry agent here IS allowed to edit.
    $trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) {
        $failures += "[$label] no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
        continue
    }

    # DOTTED navigation - the TRX carries a default xmlns, so SelectNodes('//UnitTestResult') returns
    # NOTHING. The Where-Object is load-bearing: with zero tests executed the TRX has no <Results>
    # element, the navigation yields $null, and @($null).Count is ONE - so the bare form would make
    # the guard below evaluate 1 -lt 1 and never fire.
    $xml      = [xml](Get-Content $trx.FullName -Raw)
    $recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($recorded.Count -lt 1) {
        $failures += "[$label] the TRX records ZERO executed tests - the --filter '$($suite.Filter)' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
        continue
    }

    foreach ($behaviour in $suite.Manifest.Keys) {
        $entry  = $suite.Manifest[$behaviour]
        $name   = if ($entry -is [string]) { $entry }   else { $entry.Name }
        $expect = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }

        # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not. The (\(|$) tail
        # admits a [Theory] row's appended data without admitting a longer sibling name.
        $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
        $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
        if ($hits.Count -lt 1) {
            $failures += "[$label] $behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter '$($suite.Filter)')"
            continue
        }

        if ($expect -eq 'Executed') {
            # DECLARED EXEMPTION: assert the row RAN, not that it was red. An absent outcome attribute
            # is treated as not-executed - never let a missing value read as satisfied.
            $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
            if ($notRun.Count -gt 0) {
                $failures += "[$label] $behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - this file's header says why a correct test is green on the stub tree) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all, and these two are the negative pins."
            }
            continue
        }

        $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
        if ($notRed.Count -gt 0) {
            $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "[$label] $behaviour -> '$name' is $seen on the STUB tree, not Failed. Poll() and Rebaseline() throw NotImplementedException, so a test that does not fail here never invoked them - it asserts a tautology and certifies nothing. Drive the real API and assert the outcome. ('NotExecuted' = [Fact(Skip=...)].)"
        }
    }

    Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue
}

$total = ($suites | ForEach-Object { $_.Manifest.Count } | Measure-Object -Sum).Sum
if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) problem(s) across $total enumerated behaviours ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Census clean: all $total enumerated behaviours are bound to a pinned test with the declared outcome (11 Failed, 2 declared-exempt Executed)."
exit 0
