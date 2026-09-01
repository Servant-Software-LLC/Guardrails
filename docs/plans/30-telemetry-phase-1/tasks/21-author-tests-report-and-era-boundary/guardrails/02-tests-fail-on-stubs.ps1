# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a string the test itself built, an assertion on the fixture row rather than on the report's
#          rendered stdout). It PASSES against today's TelemetryCommand and hides behind its genuinely-
#          failing siblings, so a suite-level non-zero exit would certify the file honest (#375). One
#          entry per enumerated behaviour, each observed Failed in the runner's OWN TRX - never merely
#          discovered by name, which a hollow body satisfies exactly as a comment satisfies a token floor.
#
#          The near-miss specific to THIS file is the fixture-shaped tautology: every test here writes
#          corpus rows and then reads a rendered table, and a test that asserts on the row it appended
#          instead of on the CLI's stdout is green today, green after task 22, and evidence of nothing.
#          Four of the five behaviours are red on the current CLI precisely because the CLI does not
#          render the thing asserted; a test that never reads stdout cannot be red for that reason.
#
# ONE DECLARED EXEMPTION, stated here because the census's own failure text points a retry agent back at
#          this header: 'AnUnbucketedLegacyRow_StillRendersUnbucketed' asserts the SURVIVING half of the
#          honesty rule - a row carrying no bucket keeps rendering the (unbucketed) sentinel - and
#          TelemetryCommand.cs line 435 already assigns UnbucketedBucket to every sample unconditionally.
#          So a CORRECT test is GREEN before task 22 lands. Demanding red there would demand a correct
#          test fail, and would push an author toward dating its fixture BEFORE the era boundary to
#          manufacture one - which is the exact trap the action prompt warns about, because behaviour 5's
#          filter would then remove the row and the test would observe nothing forever.
#
#          The row therefore asserts Expect='Executed' (it ran, and was not [Skip]ped) and stays IN the
#          manifest: a dropped row and an oversight look identical. It exists because task 22 rewrites
#          the very line that renders that cell, and section 5 of the plan puts "any change to the
#          report's honesty rules" OUT of scope - this is the check that stops the (unbucketed) case
#          being deleted on the way past. The other four rows are red on today's CLI: it renders no
#          bucket from the row, folds no digest into the fingerprint, and prints no boundary at all.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The five names below were read side by side with this task's
#          action.prompt.md table, which pins each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend on
#          it - kept anyway so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair's OWN test class, never a plan-wide trait (#455). This plan introduces no trait at all, so
# this is shape 3 - the class term alone. 'TelemetryReportPhase1Tests' was checked against all 328
# existing test class names in the repo and every other class this plan authors: it is a substring of
# none of them and contains none of them (in particular it is neither a sub- nor a super-string of the
# shipped TelemetryReportTests), so the filter is discriminating in both directions.
$filter = 'FullyQualifiedName~TelemetryReportPhase1Tests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'a bucketed row renders its bucket, not (unbucketed)'          = 'ABucketedRow_RendersItsBucket_NotUnbucketed'
    # DECLARED EXEMPTION - see this file's header. The report already renders (unbucketed) for every
    # sample, so a CORRECT test is green on today's CLI. Assert it RAN, never that it failed.
    'an unbucketed post-boundary row still renders (unbucketed)'   = @{ Name = 'AnUnbucketedLegacyRow_StillRendersUnbucketed'; Expect = 'Executed' }
    'two digests under one model tag do not pool'                  = 'TwoDigestsUnderOneModelTag_DoNotPool'
    'the report states the era boundary date'                      = 'TheReportStatesTheEraBoundaryDate'
    'rows before the era boundary are excluded from the table'     = 'RowsBeforeTheEraBoundary_AreExcludedFromTheStratifiedTable'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

$out = dotnet test tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj --filter $filter --nologo `
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
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter)"
        continue
    }
    if ($expect -eq 'Executed') {
        # DECLARED EXEMPTION: assert the row RAN, not that it was red. An absent outcome attribute is
        # treated as not-executed - never let a missing value read as satisfied.
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct test is green against today's CLI) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all, and this is the row that stops the (unbucketed) legend case being deleted by task 22."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the current CLI, not Failed. Today TelemetryCommand assigns (unbucketed) to every sample unconditionally (line 435), builds its fingerprint from kind/runner/model with no digest (line 455), and prints no era boundary anywhere - so a test that reads the report's rendered stdout CANNOT be green for this behaviour. A green here means the test asserts on the fixture row it appended rather than on the output, or never invokes 'telemetry report' at all. Invoke the verb and assert on io.OutText. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the current CLI ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "Per-test red census: all $($manifest.Count) enumerated behaviours are bound to a pinned test, four observed Failed against the current CLI and the declared exemption observed executed."
exit 0
