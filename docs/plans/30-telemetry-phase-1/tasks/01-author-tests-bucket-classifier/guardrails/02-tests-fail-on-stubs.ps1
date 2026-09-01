# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, any assertion that never calls Classify). It PASSES
#          against the NotImplementedException stub and hides behind its genuinely-failing siblings, so
#          a suite-level non-zero exit would certify the file honest (#375). One entry per enumerated
#          behaviour, each observed Failed in the runner's OWN TRX - never merely discovered by name,
#          which a hollow body satisfies exactly as a comment satisfies a token floor.
#
# ONE DECLARED EXEMPTION, stated here because the census's own failure text points a retry agent back
#          at this header: 'ClassifySignatureAdmitsNoTaskIdentity' reads the signature by REFLECTION and
#          never calls Classify, so it is GREEN against the stub WHEN IT IS CORRECT - the stub already
#          carries the pinned two-parameter signature. Demanding red there would demand a correct test
#          fail. The row therefore asserts Expect='Executed' (it ran, and was not [Skip]ped) and stays
#          IN the manifest: a dropped row and an oversight look identical. The other nine rows call
#          Classify against a stub that throws unconditionally, so a correct test is red for all nine.
#
#          That reflection test is also why this task ships NO source-shape regex for the report
#          legend's "a bucket is a fact about a task, never one read off its name" constraint. The
#          property is observable at runtime through the parameter list, so the demotion gate's rung 1
#          applies and a test carries it - and unlike a grep it cannot be satisfied by a comment.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The nine names below were read side by side with this task's
#          action.prompt.md table, which pins each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend
#          on it - kept anyway so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair's OWN test class, never a plan-wide trait (#455). This plan introduces no trait at all, so
# this is shape 3 - the class term alone. 'TaskFingerprintBucketTests' was checked against all 195
# existing Core test class names and every other class this plan authors: it is a substring of none of
# them, so the filter is discriminating.
$filter = 'FullyQualifiedName~TaskFingerprintBucketTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'an EMPTY writeScope is no-write'                              = 'EmptyWriteScope_IsNoWrite'
    'a NULL writeScope is null, not no-write'                      = 'NullWriteScope_IsNull_NotNoWrite'
    'tests-only + a TDD-red guardrail is test-authoring'           = 'TestsOnlyWithATddRedGuardrail_IsTestAuthoring'
    'src-only gated by tests-pass is implementation'               = 'SrcOnlyGatedByTestsPass_IsImplementation'
    'src-only with no behavioural gate is structural'              = 'SrcOnlyWithNoBehaviouralGate_IsStructural'
    'tests-only with no behavioural gate is structural'            = 'TestsOnlyWithNoBehaviouralGate_IsStructural'
    'src AND tests is code+tests even with a TDD-red guardrail'    = 'BothSrcAndTests_IsCodePlusTests_EvenWithATddRedGuardrail'
    'docs/.claude only is documentation'                           = 'DocsOrClaudeOnly_IsDocumentation'
    'an unmatched write surface is null, so the reader unbuckets'  = 'AWriteSurfaceNoRuleMatches_IsNull'
    # DECLARED EXEMPTION - see this file's header. Reads the signature by reflection, never calls
    # Classify, so a CORRECT test is green on the stub tree. Assert it RAN, never that it failed.
    'Classify admits no task identity (reflection over the signature)' = @{ Name = 'ClassifySignatureAdmitsNoTaskIdentity'; Expect = 'Executed' }
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
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter)"
        continue
    }
    if ($expect -eq 'Executed') {
        # DECLARED EXEMPTION: assert the row RAN, not that it was red. An absent outcome attribute is
        # treated as not-executed - never let a missing value read as satisfied.
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct test is green against the stub) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the STUB tree, not Failed. A test that does not fail against the NotImplementedException stub never calls Classify, so it asserts a tautology and certifies nothing. Call TaskFingerprintBucket.Classify and assert its return value. ('NotExecuted' = [Fact(Skip=...)].)"
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
