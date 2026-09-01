# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          about the fixture the test itself just wrote to disk, any assertion that never calls Census).
#          It PASSES against the NotImplementedException stub and hides behind its genuinely-failing
#          siblings, so a suite-level non-zero exit would certify the file honest (#375). One entry per
#          enumerated behaviour, each observed Failed in the runner's OWN TRX - never merely discovered
#          by name, which a hollow body satisfies exactly as a comment satisfies a token floor.
#
#          The sharpest hollow shape THIS pair invites: behaviours 6 and 7 are about FAULT TOLERANCE
#          ("is skipped, not fatal" / "is a reported no-op"), and the cheapest way to write either is to
#          assert that nothing threw. Against a stub that throws NotImplementedException that assertion
#          is red for the wrong reason today and green forever after, whatever Census does with the
#          folder. Both rows are in the manifest below for the same reason as the other five: they must
#          call Census and assert on UnreadableDefinitions / SkippedFolders by NAME.
#
# NO EXEMPTIONS in this pair, and that is a deliberate statement rather than an omission. Every one of
#          the seven behaviours goes through Census, and the stub throws UNCONDITIONALLY, so a correct
#          test is red for all seven - there is no reflection-only or already-satisfied row here of the
#          kind tasks 01, 07, 09, 11, 13 and 19 each had to declare.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The seven names below were read side by side with this task's
#          action.prompt.md table, which pins each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend
#          on it - kept anyway so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair's OWN test class, never a plan-wide trait (#455). This plan introduces no trait at all, so
# this is shape 3 - the class term alone. 'AttributionCensusTests' was checked against all 197 existing
# Core test class names and every other class this plan authors: it is a substring of none of them, so
# the filter is discriminating. (The Integration project's OverlappingWriteScopeAttributionTests is in a
# different assembly this filter never reaches, and does not contain the term in any case.)
$filter = 'FullyQualifiedName~AttributionCensusTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'a task-grain sentinel row is correct by construction'          = 'ATaskGrainSentinelRow_CountsAsCorrectByConstruction'
    'a script action attempt is correct by construction'            = 'AScriptActionAttempt_CountsAsCorrectByConstruction'
    'a prompt attempt with no provenance is the recording gap'      = 'APromptAttemptWithNoProvenance_CountsAsARecordingGap'
    'a prompt attempt naming a model counts in no category'         = 'APromptAttemptWithProvenance_CountsInNoCategory'
    'the three categories sum to the total naming no model'         = 'TheThreeCategoriesSumToTheTotalNamingNoModel'
    'one malformed task.json is skipped, not fatal'                 = 'AMalformedTaskJson_IsSkipped_NotFatal'
    'a plan folder with no journal is a reported no-op'             = 'APlanFolderWithNoJournal_IsAReportedNoOp'
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
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the STUB tree, not Failed. TelemetryAttributionCensus.Census throws NotImplementedException unconditionally, so a test that does not fail against it never calls Census - it asserts a tautology and certifies nothing. Call TelemetryAttributionCensus.Census(<a real plan folder written to a temp directory>) and assert on the returned AttributionCensusResult. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the stub ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "Per-test red census: all $($manifest.Count) enumerated behaviours are bound to a pinned test observed Failed against the stub."
exit 0
