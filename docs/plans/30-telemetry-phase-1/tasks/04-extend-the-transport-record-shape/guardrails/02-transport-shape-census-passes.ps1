# catches: a transport hop that is DECLARED but not proven - and, distinctly, a pinned behaviour that
#          quietly stopped existing. A suite-level green exit certifies only that the tests which RAN
#          passed; a test deleted, renamed, or [Skip]ped leaves the suite green and the behaviour
#          unobserved. The row this protects hardest is
#          EveryPendingAttemptCarrierHasAnAttemptRecordCounterpart: it is the trace-the-datum rule made
#          a test, and it is precisely the check an agent under retry pressure would delete to make the
#          suite green. One entry per enumerated behaviour, each observed 'Passed' in the runner's OWN
#          TRX - never merely discovered by name.
#
# FORWARD census, and here is why this pair has no red rung. This is a COLLAPSED TDD pair over a pure
#          data model, the same exemption as task 03: the record declaration IS the implementation, so
#          there is no stub-versus-real distinction to be red about - a member either exists (the test
#          compiles and passes) or it does not (the test does not compile, which guardrail 01 catches).
#          Step 2 criterion (c) names this as the exemption to the authorship split. The consequence,
#          stated rather than glossed: the anti-tautology protection here is WEAKER than a stub-based
#          pair's, because nothing throws and a set-it-then-read-it-back test is close to hollow. It is
#          carried instead by row 5, a REFLECTION test across two types that no hollow body satisfies,
#          and by the defaults-to-null half of rows 1-4, which catches an eagerly-defaulted member that
#          would make every unreported attempt CLAIM a measurement never taken.
#
# NO EXEMPTIONS. All five rows demand 'Passed'.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The five names below were read side by side with this task's
#          action.prompt.md table, which pins each one VERBATIM.
#
# Re-emits the assertion/exception lines at the END of stdout (#179) on the failing branch, because
#          this guardrail ASSERTS TESTS PASS: default `dotnet test` prints the Error Message/Expected/
#          Actual block mid-run and ends with only [FAIL] <name>, which does not reach the harness's
#          ~60-line retry-feedback tail.
#
# NO -v q on the TEST command (#179/#462): it suppresses that entire block, leaving nothing for the
#          re-emit to find. -v q is correct on a `dotnet build` and only there.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:'), which would invert the
# zero-match guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this guardrail runs this task's own tests and cannot run without it."
    exit 1
}

# This task's OWN test class, never a plan-wide trait (#455): a trait-only filter asserts the state of
# every test in the plan, so this task could not go green until a task that DEPENDS on it had run - a
# deadlock validate and graph --check cannot see. This plan introduces no trait at all, so this is
# shape 3, the class term alone. 'TransportShapeTests' was checked against all 194 Core test class
# names in the tree today and against every class this plan authors: it is a substring of none of them,
# so the filter is discriminating.
$filter = 'FullyQualifiedName~TransportShapeTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'PromptResult carries a ModelDigest'                            = 'PromptResultCarriesAModelDigest'
    'ActionRun carries the digest, turns and action duration'       = 'ActionRunCarriesTheDigestTurnsAndActionMs'
    'GuardrailRunResult carries the guardrail duration'             = 'GuardrailRunResultCarriesGuardrailMs'
    'PendingAttempt carries turns, segments and the bucket'         = 'PendingAttemptCarriesTurnsSegmentsAndBucket'
    'every PendingAttempt carrier has a next-hop counterpart'       = 'EveryPendingAttemptCarrierHasAnAttemptRecordCounterpart'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-transport-shape-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

$log = & dotnet test $project --nologo --filter $filter `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# EXIT CODE FIRST on a forward (assert-pass) check (#455): a test host that never started exits
# NON-zero with no summary and no TRX at all, so checking the exit code before the filter guard reports
# its real error instead of blaming the filter. The [FAIL] lines the re-emit below surfaces already
# name WHICH tests are red, so the census loop is not repeated here.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "TransportShapeTests is red (or the test host failed to start). This pair is collapsed: the member declarations ARE the implementation, so a failing test means a declaration and a test disagree - fix whichever is wrong, all five files are in this task's writeScope. If EveryPendingAttemptCarrierHasAnAttemptRecordCounterpart is the failure, a PendingAttempt carrier has no member of the same name on Journal.AttemptRecord or Journal.TaskJournalEntry - Bucket's counterpart is on TaskJournalEntry (task grain), and a test demanding AttemptRecord alone is the wrong test."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count
# [Skip]ped tests, so a fully-skipped class would clear a Total-keyed guard.
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. The class TransportShapeTests was not found, or the filter is malformed - this guardrail is certifying nothing. This is NOT a finding about the transport records."
    exit 1
}

# PRECONDITION - a legitimate early exit. No TRX means there are no per-test results to census, which
# is a runner/logger problem and not a finding about the tests. Falling through would print "every
# behaviour unbound", a confident wrong message aimed at the one artifact a retry agent may edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir even though the run exited 0 with $executed test(s) executed - the trx logger did not write results. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
# navigation yields $null, and @($null).Count is 1 - so the bare @(...) form would make the guard below
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
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, renamed, or not selected by the filter). The name is pinned by this task's action.prompt.md table; rename the test back rather than editing this manifest."
        continue
    }
    # An absent outcome attribute is treated as NOT passed - never let a missing value read as satisfied.
    $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notGreen.Count -gt 0) {
        $seen = (($notGreen | ForEach-Object { if ([string]::IsNullOrEmpty($_.outcome)) { '(no outcome recorded)' } else { $_.outcome } } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen, not Passed. ('NotExecuted' = [Fact(Skip=...)] - a skipped behaviour is no coverage at all, and this collapsed pair has no exemptions.)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven Passed ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "Per-test census: all $($manifest.Count) enumerated transport-shape behaviours are bound to a pinned test observed Passed ($executed test(s) executed)."
exit 0
