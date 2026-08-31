# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, any assertion that never invokes the subject). It
#          PASSES against today's code and hides behind its genuinely-failing siblings, so a
#          suite-level non-zero exit certifies the file honest while proving nothing (#375). One entry
#          per enumerated behaviour, each observed Failed in the runner's OWN TRX - never merely
#          discovered by name, which a hollow body satisfies exactly as a comment satisfies a token
#          floor. This is the strongest anti-tautology check this stage has, and stage 2's entire
#          verdict rests on these tests being real.
#
# NAMED tests-fail-on-CURRENT-CODE, not -on-stubs, and the difference is real: plan 31 section 7 says "No stub
#          stage is needed for #554". There are no stubs. Every pin here compiles against today's
#          assemblies and fails because the FEATURE is absent, not because a member throws.
#
# DECLARED EXEMPTIONS (each row's reason, because the census's failure text points a retry agent back
#          at this header, and an exemption whose reason nobody can read is indistinguishable from a
#          row somebody quietly deleted):
#   C4 PriorAttemptWithoutPatch_ComposedPromptCarriesNoRecoveryBlock - asserts the ABSENCE of a
#      recovery block for a patch-less prior. Today there is no recovery block at all, so a CORRECT
#      test is GREEN on current code; demanding red would demand a correct implementation fail.
#   I6 NeedsHumanHavingWrittenNothingInScope_LeavesNoPatchNoRefAndNoSalvageSection - same shape:
#      today NOTHING is preserved on the escalation path, so "leaves nothing" is green when correct.
#   I7 SerialMode_EscalationPathPreservesNothing - same shape; serial mode is documented unchanged
#      (plan section 3.4), so this row is green before AND after and its job is to stay that way.
#   All three assert Expect='Executed' (present in the TRX and not [Skip]ped). They stay IN the
#   manifest: a dropped row and an oversight look identical from the outside.
#
# This script's own exit code is FORWARD - 0 when every manifested behaviour is bound as declared. Do
#          NOT add an `if ($testExit -eq 0)` branch from the inverse form: the suite exit is the very
#          signal that hid the defect, and the census subsumes it.
$ErrorActionPreference = 'Continue'

# The census reads the TRX (schema tokens, NOT localized), so unlike a summary-line guard it does not
# depend on this - keep it so the logged output is readable and the pair stays copy-pasteable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# A BARE STRING means Expect='Failed' - the default. A HASHTABLE declares an EXEMPTION.
$suites = @(
    @{
        Project  = 'tests/Guardrails.Core.Tests'
        Filter   = 'FullyQualifiedName~EscalationSalvageTests'
        Label    = 'Core'
        Manifest = [ordered]@{
            'C1 the composed prompt carries the SIZE-ROUTED recovery choice'   = 'PriorAttemptWithPatch_ComposedPromptCarriesSizeRoutedRecoveryChoice'
            'C2 the composed prompt carries the writeScope caveat'             = 'PriorAttemptWithPatch_ComposedPromptCarriesTheWriteScopeCaveat'
            'C3 the composed prompt names the DERIVED salvage ref'             = 'PriorAttemptWithPatch_ComposedPromptNamesTheDerivedSalvageRef'
            'C4 no patch => NO recovery block (empty-diff silence)'            = @{ Name = 'PriorAttemptWithoutPatch_ComposedPromptCarriesNoRecoveryBlock'; Expect = 'Executed' }
        }
    },
    @{
        Project  = 'tests/Guardrails.Integration.Tests'
        Filter   = 'FullyQualifiedName~EscalationSalvageTests'
        Label    = 'Integration'
        Manifest = [ordered]@{
            'I1 needsHuman after writing leaves a NON-EMPTY prior-attempt.patch' = 'NeedsHumanAfterWritingFiles_LeavesANonEmptyPriorAttemptPatch'
            'I2 ...and a salvage ref for that attempt'                           = 'NeedsHumanAfterWritingFiles_LeavesASalvageRefForTheAttempt'
            'I3 an OUT-OF-SCOPE write is ABSENT from the patch and the ref tree' = 'NeedsHumanWithAnOutOfScopeWrite_ThatWriteIsAbsentFromThePatchAndTheRefTree'
            'I4 a needsHuman on a FINAL attempt still preserves'                 = 'NeedsHumanOnTheFinalAttempt_StillPreserves'
            'I5 the escalation Context names the ref and the patch'              = 'NeedsHumanEscalation_ContextNamesTheRefAndThePatch'
            'I6 nothing written in scope => no patch, no ref, no section'        = @{ Name = 'NeedsHumanHavingWrittenNothingInScope_LeavesNoPatchNoRefAndNoSalvageSection'; Expect = 'Executed' }
            'I7 serial mode preserves nothing (unchanged)'                       = @{ Name = 'SerialMode_EscalationPathPreservesNothing'; Expect = 'Executed' }
            'I8 repeat escalations: refs capped but NOT empty'                   = 'RepeatEscalations_SalvageRefsAreCappedButNotEmpty'
            'I9 the salvage text says ORPHANED and never claims a rollback'      = 'NeedsHumanEscalation_SalvageTextSaysOrphanedAndNeverClaimsARollback'
        }
    }
)

$failures = @()

foreach ($suite in $suites) {
    $label      = $suite.Label
    $resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr31-census-$label-$PID"
    # --results-directory is NOT cleared between runs: a stale TRX from a previous attempt would be
    # read as THIS attempt's evidence. Delete it first.
    Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

    # No -v q: pointless here (nothing is re-emitted) and it propagates onto forward checks by
    # cloning a sibling file (#462).
    $out = & dotnet test $suite.Project --nologo --filter $suite.Filter `
           --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
    $out | ForEach-Object { Write-Output $_ }

    # PRECONDITION - the ONE legitimate early exit per suite. No TRX means the run never happened
    # (test host failed to start, wrong project path, or a MALFORMED --filter, which exits 0
    # SILENTLY). Diagnose THAT. Falling through would print "every behaviour unbound" - a confident,
    # actionable, wrong message aimed at the one artifact a retry agent is allowed to edit.
    $trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) {
        $failures += "[$label] no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
        continue
    }

    # DOTTED navigation - the TRX carries a default xmlns, so SelectNodes('//UnitTestResult') returns
    # NOTHING. The Where-Object is NOT decoration: with zero tests executed the TRX has no <Results>
    # element at all, the navigation yields $null, and @($null).Count is ONE - so the bare @(...) form
    # makes the guard below evaluate 1 -lt 1 and NEVER FIRE.
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
                $failures += "[$label] $behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - this file's header says why a correct implementation leaves it green) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
            }
            continue
        }

        $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
        if ($notRed.Count -gt 0) {
            $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "[$label] $behaviour -> '$name' is $seen on today's code, not Failed. Nothing is preserved on the escalation path today (TaskExecutor.cs:838-843 short-circuits before any salvage call), so a test that does not fail here never invokes the subject and asserts a tautology. Drive the real path and assert the artifact. ('NotExecuted' = [Fact(Skip=...)].)"
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
Write-Output "Census clean: all $total enumerated behaviours are bound to a pinned test with the declared outcome."
exit 0
