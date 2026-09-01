# catches: a HOLLOW test - named for the behaviour, body a tautology. The shape this pair is most
#          exposed to is NOT Assert.True(true): it is the AGREEMENT test written as
#          Assert.Equal(first.Bucket, second.Bucket). On today's unwired tree BOTH sides are null, so
#          null == null and the test is GREEN while certifying nothing. Two of the five behaviours
#          below (NotFromTheTaskName, StableAcrossARetry) are exactly that shape, which is why every
#          row must be observed FAILED in the runner's OWN TRX - never merely discovered by name, which
#          a hollow body satisfies exactly as a comment satisfies a token floor. A suite-level non-zero
#          exit would certify the file honest on the strength of its genuinely-failing siblings (#375).
#
# NO EXEMPTIONS, and that is a claim about this tree, not a default. All five behaviours read
#          TaskJournalEntry.Bucket after a real settle, and nothing populates it today: AttemptJournaler
#          never computes it and RunJournal's three recorders have nowhere to put it (that wiring is
#          task 06-journal-the-bucket-serial). Every correct test is therefore red right now. A row
#          reported green here is a hollow test, not an exempt one.
#
# This pair authors NO STUB and needs none: 02-implement-bucket-classifier shipped
#          TaskFingerprintBucket.Classify and 03-extend-the-journal-record-shape shipped
#          TaskJournalEntry.Bucket, so the tests COMPILE against today's tree (guardrail 01 proves it)
#          and their red is a RUNTIME red. That is the distinction this census depends on: a compile
#          failure would exit dotnet test non-zero identically to a behavioural failure.
#
# GUARD FIRST, exit code never: this is an INVERSE check. A correct tree here exits dotnet test
#          NON-ZERO by construction, so an exit-code test would be inverted noise. The preconditions
#          (no TRX / zero recorded results) run before the census instead, because a run that never
#          happened must not be reported as five unbound behaviours.
#
# NO #179 re-emit: that rule governs guardrails that assert tests PASS, where the assertion detail is
#          the actionable payload. Here the failures are the SUCCESS condition; re-emitting them would
#          bury the census's own findings under the very output it is asserting.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The five names below were read side by side with this task's
#          action.prompt.md table, which pins each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend
#          on it - kept anyway so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair's OWN test class, never a plan-wide trait (#455). This plan introduces no trait at all, so
# this is shape 3 - the class term alone. 'TaskBucketJournalTests' was checked against all 194 Core
# test class names in the tree today and against every other class this plan authors: it is a substring
# of none of them, so the filter is discriminating.
$filter = 'FullyQualifiedName~TaskBucketJournalTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'a succeeded serial settle journals the bucket'                 = 'SucceededSettle_JournalsTheBucket'
    'a failed attempt journals the bucket too'                      = 'FailedAttempt_JournalsTheBucketToo'
    'the bucket comes from writeScope and guardrails, not the name' = 'TheBucketIsComputedFromWriteScopeAndGuardrails_NotFromTheTaskName'
    'a task that writes nothing journals no-write'                  = 'ATaskThatWritesNothing_JournalsNoWrite'
    'the bucket is stable across a retry of the same task'          = 'TheBucketIsStableAcrossARetryOfTheSameTask'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-bucket-journal-red-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

$out = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
# start, wrong project path, or a malformed --filter, which exits 0 SILENTLY). Diagnose THAT. Falling
# through would print "every behaviour unbound", a confident wrong message aimed at the one artifact a
# retry agent is allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
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
    # An absent outcome attribute is treated as NOT failed - never let a missing value read as satisfied.
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { if ([string]::IsNullOrEmpty($_.outcome)) { '(no outcome recorded)' } else { $_.outcome } } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the UNWIRED tree, not Failed. Nothing populates TaskJournalEntry.Bucket yet, so a test that reads it after a real settle MUST be red. A green one is reading null and calling it agreement - assert the CONCRETE expected bucket (a TaskFingerprintBucket constant) on every entry, never merely that two entries are equal to each other. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the unwired tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "Per-test red census: all $($manifest.Count) enumerated behaviours are bound to a pinned test observed Failed against the unwired journal."
exit 0
