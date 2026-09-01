# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.Null on
#          a member nothing was ever asked to populate, any assertion that never drives the real
#          OpenAiCompatPromptRunner). It PASSES on today's tree and hides behind its genuinely-failing
#          siblings, so a suite-level non-zero exit would certify the file honest (#375). One entry per
#          enumerated behaviour, each observed Failed in the runner's OWN TRX - never merely discovered
#          by name, which a hollow body satisfies exactly as a comment satisfies a token floor.
#
#          The sharpest hollow shape HERE is a test that asserts on a hand-built PromptResult instead of
#          on one the runner returned. `new PromptResult { ModelDigest = "fp_x" }` round-trips its own
#          assignment and is green forever - that is precisely how AttemptRecord.Usage shipped
#          structurally dead (#475), and ObservedModelCaptureTests' own header records the lesson: the
#          CHILD PROCESS (here, the HTTP transport) is faked; the runner never is.
#
# ONE DECLARED EXEMPTION, stated here because the census's own failure text points a retry agent back
#          at this header: 'AResponseWithNoSystemFingerprint_LeavesTheDigestNull' asserts the ABSENCE
#          case, and nothing populates PromptResult.ModelDigest on today's tree - so a CORRECT test for
#          the null case is GREEN before the capture lands. Demanding red there would demand a correct
#          test fail. The row therefore asserts Expect='Executed' (it ran, and was not [Skip]ped) and
#          stays IN the manifest: a dropped row and an oversight look identical. Its job is to stay
#          green through task 08, which rewrites both fold sites and could introduce an "" or a
#          fabricated placeholder where absence belongs.
#
#          The other three rows are red on today's tree because nothing anywhere reads
#          `system_fingerprint` (zero hits repo-wide at authoring time), so any test that asserts a
#          digest ARRIVED must fail until ApplyChunk / ApplyWholeCompletion lift it.
#
# NOTE on 'TheDigestIsIndependentOfTheObservedModel': the spec sketch for this behaviour described only
#          the absent-fingerprint direction, which is green today. The prompt therefore pins it as a
#          TWO-direction test whose FIRST direction (a model and a DIFFERENT fingerprint both arriving,
#          unswapped) is red on today's tree. It is listed here as a red row for that reason. If it
#          comes back green, the test was authored with the absent direction only - re-read the prompt's
#          behaviour-4 paragraph rather than adding an exemption for it.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The four names below were read side by side with this task's
#          action.prompt.md table, which pins each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend
#          on it - kept anyway so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair's OWN test class, never a plan-wide trait (#455). This plan introduces no trait at all, so
# this is shape 3 - the class term alone. 'ModelDigestCaptureTests' was checked against all 195 existing
# Core test class names and every other class this plan authors: it is a substring of none of them (in
# particular it is NOT a substring of ModelDigestProvenanceTests, task 09's class), so the filter is
# discriminating.
$filter = 'FullyQualifiedName~ModelDigestCaptureTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'a streamed chunk carrying system_fingerprint sets the digest'   = 'AStreamedChunkCarryingASystemFingerprint_SetsTheModelDigest'
    'a whole completion carrying system_fingerprint sets it too'     = 'AWholeCompletionCarryingASystemFingerprint_SetsTheModelDigest'
    'the digest and the observed model are two facts, not one'       = 'TheDigestIsIndependentOfTheObservedModel'
    # DECLARED EXEMPTION - see this file's header. Asserts the ABSENCE case, which is already true on a
    # tree where nothing populates the digest, so a CORRECT test is green. Assert it RAN, never that it
    # failed.
    'a response with no system_fingerprint leaves the digest null'   = @{ Name = 'AResponseWithNoSystemFingerprint_LeavesTheDigestNull'; Expect = 'Executed' }
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
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct test is green before the capture lands) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on today's tree, not Failed. Nothing reads system_fingerprint anywhere in this repo yet, so a test that asserts a digest ARRIVED cannot pass - which means this one asserts on a hand-built PromptResult instead of on one the real OpenAiCompatPromptRunner returned, or it asserts only the ABSENT direction. Drive the runner through its injected HttpClient and assert the digest it lifted off the scripted body. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on today's tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "Per-test red census: all $($manifest.Count) enumerated behaviours are bound to a pinned test observed at its declared outcome."
exit 0
