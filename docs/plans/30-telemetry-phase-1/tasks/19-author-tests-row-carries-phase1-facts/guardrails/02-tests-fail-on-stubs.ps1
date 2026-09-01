# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion on
#          a TelemetryRow the test itself constructed, any assertion that never runs TelemetryIngest).
#          It PASSES against today's unmapped ETL and hides behind its genuinely-failing siblings, so a
#          suite-level non-zero exit would certify the file honest (#375). One entry per enumerated
#          behaviour, each observed Failed in the runner's OWN TRX - never merely discovered by name,
#          which a hollow body satisfies exactly as a comment satisfies a token floor.
#
#          The near-miss specific to THIS file is the self-referential fixture: every test here builds a
#          journal, ingests it, and reads rows back, and a test that asserts on the row object it
#          constructed instead of on the row the ETL wrote is green today, green after task 20, and
#          evidence of nothing. Six of the eight behaviours are red precisely because TelemetryIngest
#          maps none of the thirteen columns; a test that never calls Ingest cannot be red for that
#          reason.
#
#          This census also REPLACES the source-shape [Fact]-presence check an earlier draft of this task
#          carried, and the reason is the #468 demotion gate resolving correctly rather than a relaxation.
#          That draft existed because this pair's red was a COMPILE red, so nothing in the file could
#          execute, no TRX existed, and reading the source was the only observable available. Task 04a
#          landed the row shape first, the red became an ordinary runtime red, and the property the grep
#          carried - "a real [Fact] exists for each pinned behaviour" - is now carried by rung 1: a
#          pinned name that does not RUN is already a named finding below, and unlike a grep it cannot be
#          satisfied by a comment. The guardrail and its committed sample pair were deleted with it.
#
# TWO DECLARED EXEMPTIONS, stated here because the census's own failure text points a retry agent back at
#          this header.
#
#          'TheSchemaVersionSaysTheRowShapeChanged' asserts the ETL STAMPS TelemetryRow.CurrentSchemaVersion
#          on every row it writes, and that the constant is past 1. Task 04a bumped the constant, and the
#          ETL has stamped it symbolically at both construction sites since Phase 0 - so a CORRECT test
#          is green here. It is not redundant with 04a's own TheSchemaVersionIsBumpedPastOne: that one
#          reads the constant, this one proves the emitted rows carry it, and a mapping that wrote a
#          literal would pass the first and fail the second.
#
#          'AnUnreportedPhase1Fact_StaysNull_NotZero' asserts a journal reporting none of the Phase-1
#          attempt facts leaves the columns null rather than 0/false. The columns exist (04a) and nothing
#          populates them, so they are already null and a CORRECT test is green. Its value is entirely
#          forward-looking: after task 20 it is the check that stops the mapping coalescing an unreported
#          fact into a value (`?? 0`, `?? false`), which is section 15.2's null-versus-zero rule.
#
#          Demanding red on either would demand a correct test fail, and would push an author toward
#          asserting something false to manufacture one. Both rows assert Expect='Executed' (they ran,
#          and were not [Skip]ped) and stay IN the manifest: a dropped row and an oversight look
#          identical. The other six drive an ETL that maps nothing, so a correct test is red for all six.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The eight names below were read side by side with this task's
#          action.prompt.md table, which pins each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend on
#          it - kept anyway so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair's OWN test class, never a plan-wide trait (#455). This plan introduces no trait at all, so
# this is shape 3 - the class term alone. 'Phase1TelemetryRowTests' was checked against all 328 existing
# test class names in the repo and every other class this plan authors: it is a substring of none of them
# and contains none of them, so the filter is discriminating in both directions.
$filter = 'FullyQualifiedName~Phase1TelemetryRowTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'the attempt row carries the task bucket'                   = 'TheAttemptRowCarriesTheBucket'
    'the Attempt==0 task-grain row carries the bucket too'      = 'TheTaskGrainRowCarriesTheBucketToo'
    'the attempt row carries the model digest'                  = 'TheAttemptRowCarriesTheModelDigest'
    'the attempt row carries turns and both segment durations'  = 'TheAttemptRowCarriesTurnsAndSegments'
    'the attempt row carries route warmth, both polarities'     = 'TheAttemptRowCarriesRouteWarmth'
    'every row of both grains carries the run environment'      = 'EveryRowCarriesTheRunEnvironment'
    # DECLARED EXEMPTION - see this file's header. 04a bumped the constant and the ETL already stamps it
    # symbolically, so a CORRECT test is green. Assert it RAN, never that it failed.
    'the ETL stamps the bumped schema version on every row'     = @{ Name = 'TheSchemaVersionSaysTheRowShapeChanged'; Expect = 'Executed' }
    # DECLARED EXEMPTION - see this file's header. The columns exist and nothing populates them, so they
    # are already null; the row's value is forward-looking, against task 20 coalescing them.
    'an unreported Phase-1 fact stays null, never 0 or false'   = @{ Name = 'AnUnreportedPhase1Fact_StaysNull_NotZero'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
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
    $entry  = $manifest[$behaviour]
    $name   = if ($entry -is [string]) { $entry }   else { $entry.Name }
    $expect = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, renamed, or not selected by the filter). The name is pinned by this task's action.prompt.md table; rename the test back rather than editing this manifest."
        continue
    }
    if ($expect -eq 'Executed') {
        # DECLARED EXEMPTION: assert the row RAN, not that it was red. An absent outcome attribute is
        # treated as not-executed - never let a missing value read as satisfied.
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct test is green before task 20 lands) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the unmapped ETL, not Failed. TelemetryIngest maps NONE of the thirteen Phase-1 columns today - both of its 'new TelemetryRow { ... }' sites stop where Phase 0 left them - so a test that reads a row back out of a real TelemetryCorpusStore CANNOT be green for this behaviour. A green here means the test asserts on a TelemetryRow it constructed itself rather than on the row the ETL wrote, or never calls TelemetryIngest.Ingest at all. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the unmapped ETL ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "Per-test red census: all $($manifest.Count) enumerated behaviours are bound to a pinned test - six observed Failed against the unmapped ETL, two declared exemptions observed executed."
exit 0
