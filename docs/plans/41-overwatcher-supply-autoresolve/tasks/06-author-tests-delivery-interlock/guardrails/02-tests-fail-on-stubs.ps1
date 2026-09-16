# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          that never invokes RunOutcomePolicy). It PASSES against the NotImplementedException predicate
#          and hides behind its genuinely-failing siblings, so a suite-level non-zero exit certifies the
#          file honest (#375). One entry per enumerated behaviour, each observed Failed in the runner's
#          OWN TRX - never stdout, never --list-tests, both of which a hollow body satisfies.
#
#          BOUNDARY: this proves each test is COUPLED TO THE CODE PATH, not that its assertion is
#          correct. An invoking-then-hollow test is red on stubs, green after, and PASSES this.
#
# NO DECLARED EXEMPTIONS, and that is a measured claim rather than an oversight: every pinned row calls
#          SuppressingDecision / SuppressingDecisionForDelivery over a NON-EMPTY decisions stream, so
#          Enumerable.FirstOrDefault(source, predicate) reaches the throwing HoldsDelivery predicate. The
#          one shape that would need an exemption - a row asserting over an EMPTY stream, where
#          FirstOrDefault never invokes the predicate and returns null without throwing - is deliberately
#          not among the pinned behaviours (the prompt says so, and the file's pre-existing rows already
#          cover the empty case).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Culture pin FIRST. This census reads the TRX (schema tokens, NOT localized) so the guard below does not
# depend on it - keep it anyway so the logged summary is readable and the pair stays copy-pasteable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The pair owns TWO test classes, so the class term is a parenthesised alternation with a BARE '|'.
# Never escape it: '\|' is VSTest's escape character and yields "Incorrect format for TestCaseFilter",
# which exits 0 with ZERO tests - a silent green (#455). The plan-wide trait is a CONJUNCT here, never
# the whole filter: trait-alone would go green off a sibling pair's red tests.
# Both class substrings were checked for discrimination against every other test class in the solution:
# 'RunOutcomePolicyTests' and 'WaveScopedInterlockTests' each match exactly one class (measured).
$filter = 'Category=OverwatchSupply&(FullyQualifiedName~RunOutcomePolicyTests|FullyQualifiedName~WaveScopedInterlockTests)'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'run end: an auto-supplied entry holds delivery'                  = 'SuppressesDelivery_True_WhenAutoSuppliedRecorded'
    'run end: the EVIDENCE is the entry, not a bool (#597)'           = 'SuppressingDecision_ReturnsTheAutoSuppliedEntry_AsTheEvidence'
    'auto-supplied holds delivery but is NOT an unreviewed wave'      = 'AutoSupplied_SuppressesDelivery_ButIsNotAnUnreviewedWave'
    'NEGATIVE: auto-applied still delivers (a different token)'       = 'AutoApplied_StillDelivers_BecauseAutoSuppliedIsADifferentToken'
    'WAVE BARRIER: an auto-supplied entry holds the barrier delivery' = 'AnAutoSuppliedDecision_HoldsTheWaveBarrierDelivery'
    'an unattributed (null wave) auto-supplied fails CLOSED'          = 'AnAutoSuppliedDecisionOutsideAnyWave_HoldsEveryDelivery'
    'scoping preserved: an uncovered wave does not hold this delivery' = 'AnAutoSuppliedDecisionInAnUncoveredWave_DoesNotHoldThisDelivery'
    'fact 11: BOTH spellings agree on every token'                    = 'BothSpellingsAgreeOnEveryToken_OverTheSameDecision'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr41-census-06-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

# No -v q: pointless here (nothing is re-emitted) and it propagates onto forward checks by cloning (#462).
$out = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
       --filter $filter --logger "trx;LogFileName=census.trx" --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

try {
    # PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
    # start, wrong project path, or a malformed --filter, which exits 0 SILENTLY). Diagnose THAT. Falling
    # through would print "every behaviour unbound" - a confident wrong message aimed at the one artifact
    # the retry agent is allowed to edit.
    $trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) {
        Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
        exit 1
    }

    # DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
    # The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
    # navigation yields $null, and @($null).Count is 1 - so the bare @(...) form would make the guard
    # below evaluate 1 -lt 1 and NEVER FIRE.
    $xml      = [xml](Get-Content $trx.FullName -Raw)
    $recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($recorded.Count -lt 1) {
        Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, is malformed, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
        exit 1
    }

    # ACCUMULATE: one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
    $failures = @()
    foreach ($behaviour in $manifest.Keys) {
        $name = $manifest[$behaviour]
        # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
        # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
        $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
        $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
        if ($hits.Count -lt 1) {
            $failures += "$behaviour -> no test named '$name' ran (absent from the file, not carrying [Trait(""Category"", ""OverwatchSupply"")], or not selected by the filter)"
            continue
        }
        $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
        if ($notRed.Count -gt 0) {
            $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "$behaviour -> '$name' is $seen on the STUB tree, not Failed. A test that does not fail against the throwing HoldsDelivery predicate never invokes it, so it asserts a tautology. Check it drives RunOutcomePolicy over a NON-EMPTY decisions stream: FirstOrDefault never calls the predicate on an empty one. ('NotExecuted' = [Fact(Skip=...)].)"
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the stubs ==="
        $failures | ForEach-Object { Write-Output "  - $_" }
        exit 1
    }

    Write-Output "Red census: all $($manifest.Count) pinned behaviour(s) observed Failed."
    exit 0
}
finally {
    Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue
}
