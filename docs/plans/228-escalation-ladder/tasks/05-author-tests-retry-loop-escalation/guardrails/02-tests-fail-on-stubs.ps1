# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, or "the stub runner was called", which a fake
#          satisfies whether or not anything is wired). It PASSES on this tree and hides behind its
#          genuinely-failing sibling, so a suite-level non-zero exit certifies the file honest while the
#          real-seam proof asserts nothing (#375). One entry per enumerated behaviour, each observed in
#          the runner's OWN TRX, never merely discovered by name.
#
# NO EXEMPTIONS. This census is 5-of-5 RED, and the two rows that used to be Expect='Executed' are the
# reason it had to change. An 'Executed' row asserts only that a test RAN, which is exactly what a
# hollow Assert.True(true) body satisfies - so the two most valuable tests in the file were the two the
# census could not read. The hole that opens is not theoretical: with both hollow, task 06 can escalate
# on a TIMEOUT with every guardrail in this plan green, breaking the charter's guardrail-failed-only
# invariant the maintainer personally settled.
# The fix is in the ACTION PROMPT, not here: each of those two behaviours now carries a CONTRAST ARM in
# the same test method, so the whole test is red before task 06 and green after.
#   * 'ATimeoutAttempt_DoesNotEscalateTheNextAttempt' - a timeout must NOT escalate (its own counter,
#     its own remedy), AND in the same fixture a GUARDRAIL failure MUST escalate. The second half is
#     red on this tree, so the row is legitimately Expect='Failed'.
#   * 'OnASingleRunnerPlan_TheSecondAttemptResolvesTheSameRouteAsTheFirst' - a config with no routing
#     block has nowhere to climb and must degrade SILENTLY (that is every plan in existence), AND the
#     same plan with a two-rung registry MUST escalate. Again the second half is red today.
#   * 'TheEscalatedAttempt_IsInvokedWithTheStrongerBlocksModel' - the silent-failure guard. A ladder
#     applied in BuildProvenance instead of at ResolveRoute writes "escalated to hard" into the journal
#     while handing the runner the EASY model: every other row here still passes and the stronger model
#     never runs. Only an assertion on the INVOCATION catches it.
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so unlike 4.3 the guard does
#          not depend on it - keep it anyway so the logged summary is readable.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=EscalationLadder&FullyQualifiedName~RetryLoopEscalationTests'   # SAME string as task 06's forward half

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
# A BARE STRING means Expect='Failed'. The HASHTABLE form (Expect='Executed') is retained by the loop
# below but DELIBERATELY UNUSED here - see the header for why no row in this file is exempt.
$manifest = [ordered]@{
    'a guardrail-failed attempt makes the next attempt one rung stronger' = 'AGuardrailFailedAttempt_MakesTheNextAttemptResolveOneRungStronger'
    'the escalated attempt records escalated + the rung it came from'     = 'TheEscalatedAttempt_RecordsTierSourceEscalatedAndTheRungItClimbedFrom'
    # SILENT-FAILURE GUARD - the journal can say "escalated" while the runner got the old model.
    'the escalated attempt is INVOKED with the stronger model'            = 'TheEscalatedAttempt_IsInvokedWithTheStrongerBlocksModel'
    # DISCRIMINATOR + its contrast arm: a timeout does not escalate, a guardrail failure does.
    'DISCRIMINATOR: a TIMEOUT does not escalate (contrast: a guardrail failure does)' = 'ATimeoutAttempt_DoesNotEscalateTheNextAttempt'
    # DEGRADE + its contrast arm: a one-block plan never climbs, a two-rung plan does.
    'DEGRADE: a single-runner plan resolves identically (contrast: a two-rung plan escalates)' = 'OnASingleRunnerPlan_TheSecondAttemptResolvesTheSameRouteAsTheFirst'
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
        $failures += "$behaviour -> '$name' is $seen on this tree, not Failed. Nothing in the retry loop escalates yet, so a test that does not fail here never asserted on the journal record an escalated attempt would write - it asserts a tautology and certifies nothing. Assert on journal.Document's attempt provenance, never on 'the stub runner was called'. If this row's behaviour is a NEGATIVE one (a timeout does not escalate; a single-runner plan does not climb), it is true on this tree by itself - the action prompt pins a CONTRAST ARM in the same test method (a guardrail failure in the same fixture DOES escalate; the same plan with a two-rung registry DOES escalate), and that arm is what makes the row red. Write both halves. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven on this tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
