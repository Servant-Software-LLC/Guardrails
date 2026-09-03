# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, any assertion that never invokes the subject). It
#          PASSES against the stubs and hides behind its genuinely-failing siblings, so a suite-level
#          non-zero exit certifies the file honest (#375). One entry per enumerated behaviour, each
#          observed Failed in the runner's OWN TRX - never merely discovered by name, which a hollow
#          body satisfies exactly as a comment satisfies a token floor.
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend on
# it - kept so the logged summary is readable and the pair stays copy-pasteable with the forward half.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=RunEvents&FullyQualifiedName~RunEventStreamTests'   # SAME string as the pair's forward half

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINS for it.
# A BARE STRING means Expect='Failed'. A HASHTABLE declares an EXEMPTION (Expect='Executed') for a row a
# CORRECT implementation leaves GREEN on the stub tree - never DROP such a row, an undeclared omission is
# indistinguishable from an oversight.
$manifest = [ordered]@{
    'AttemptFinished appends one JSON line carrying taskId/attempt/outcome' = 'AttemptFinished_AppendsOneJsonLine_CarryingTaskIdAttemptAndOutcome'
    'the outcome token matches the telemetry corpus vocabulary'             = 'AttemptFinished_OutcomeTokenMatchesTheTelemetryCorpusVocabulary'
    'every line is independently parseable JSON'                            = 'EveryLine_IsIndependentlyParseableJson'
    'an unrecognised kind is still a visible, well-formed row'              = 'UnrecognisedConsumer_StillSeesTheRow'
    'the decorator forwards every observed call to the inner'               = 'Decorator_ForwardsEveryObservedCallToTheInner'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to start,
# wrong project path, malformed --filter which exits 0 SILENTLY). Diagnose THAT; falling through would
# print "every behaviour unbound", a confident wrong message aimed at the one artifact a retry agent may edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
# navigation yields $null, and @($null).Count is 1 - so the bare @(...) form makes this guard evaluate
# 1 -lt 1 and NEVER FIRE. Measured on PowerShell 7: @($null).Count -> 1, @($null | Where-Object { $_ }).Count -> 0.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $entry   = $manifest[$behaviour]
    $name    = if ($entry -is [string]) { $entry }   else { $entry.Name }
    $expect  = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter)"
        continue
    }
    if ($expect -eq 'Executed') {
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct implementation leaves it green) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the STUB tree, not Failed. A test that does not fail against a not-yet-implemented subject never invokes it, so it asserts a tautology and certifies nothing. Drive the real API and assert the outcome. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the stubs ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
