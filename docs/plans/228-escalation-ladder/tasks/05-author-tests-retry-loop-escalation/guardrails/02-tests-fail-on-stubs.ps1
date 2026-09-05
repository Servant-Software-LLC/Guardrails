# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, or "the stub runner was called", which a fake
#          satisfies whether or not anything is wired). It PASSES on this tree and hides behind its
#          genuinely-failing sibling, so a suite-level non-zero exit certifies the file honest while the
#          real-seam proof asserts nothing (#375). One entry per enumerated behaviour, each observed in
#          the runner's OWN TRX, never merely discovered by name.
#
# DECLARED EXEMPTIONS - two rows a CORRECT implementation leaves GREEN on this tree, because nothing
# escalates yet and their whole job is to STILL be green after task 06 wires the ladder. Demanding red
# would demand a correct implementation fail. Both assert Expect='Executed' (they ran, not [Skip]ped),
# and neither is dropped: an undeclared omission is indistinguishable from an oversight.
#   * 'ATimeoutAttempt_DoesNotEscalateTheNextAttempt' - escalation triggers on guardrail-failed ONLY.
#     A timeout is evidence of SLOW work, not WRONG work, and has its own counter and its own remedy.
#     This row is the only thing that catches an over-broad trigger in task 06.
#   * 'OnASingleRunnerPlan_TheSecondAttemptResolvesTheSameRouteAsTheFirst' - a config with no routing
#     block has nowhere to climb and must degrade to today's behaviour SILENTLY. That is every plan in
#     existence, so this row is the regression guard for everyone who never asked for tiering.
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so unlike 4.3 the guard does
#          not depend on it - keep it anyway so the logged summary is readable.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=EscalationLadder&FullyQualifiedName~RetryLoopEscalationTests'   # SAME string as task 06's forward half

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
# A BARE STRING means Expect='Failed'. A HASHTABLE declares an EXEMPTION (Expect='Executed').
$manifest = [ordered]@{
    'a guardrail-failed attempt makes the next attempt one rung stronger' = 'AGuardrailFailedAttempt_MakesTheNextAttemptResolveOneRungStronger'
    'the escalated attempt records escalated + the rung it came from'     = 'TheEscalatedAttempt_RecordsTierSourceEscalatedAndTheRungItClimbedFrom'
    # DECLARED EXEMPTION - the trigger discriminator; see this file's header.
    'DISCRIMINATOR: a TIMEOUT does not escalate'                          = @{ Name = 'ATimeoutAttempt_DoesNotEscalateTheNextAttempt'; Expect = 'Executed' }
    # DECLARED EXEMPTION - the single-runner degrade; see this file's header.
    'DEGRADE: a single-runner plan resolves identically every attempt'    = @{ Name = 'OnASingleRunnerPlan_TheSecondAttemptResolvesTheSameRouteAsTheFirst'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
# No -v q: it is pointless here (nothing is re-emitted) and propagates onto forward checks by cloning (#462).
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
# start, wrong project path, malformed --filter which exits 0 SILENTLY). Diagnose THAT. Falling through
# would print "every behaviour unbound", a confident wrong message aimed at the one artifact the retry
# agent is allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
# navigation yields $null, and @($null).Count is 1 - so the bare @(...) form makes the guard below
# evaluate 1 -lt 1 and NEVER FIRE.
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
        $failures += "$behaviour -> '$name' is $seen on this tree, not Failed. Nothing in the retry loop escalates yet, so a test that does not fail here never asserted on the journal record an escalated attempt would write - it asserts a tautology and certifies nothing. Assert on journal.Document's attempt provenance, never on 'the stub runner was called'. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven on this tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
