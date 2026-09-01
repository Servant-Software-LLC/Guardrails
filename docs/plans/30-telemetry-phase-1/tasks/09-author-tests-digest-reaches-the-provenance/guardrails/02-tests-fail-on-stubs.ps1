# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion on
#          an object the test itself constructed, any "fold" test that never runs the executor). It
#          PASSES on today's tree and hides behind its genuinely-failing siblings, so a suite-level
#          non-zero exit would certify the file honest (#375). One entry per enumerated behaviour, each
#          observed Failed in the runner's OWN TRX - never merely discovered by name, which a hollow body
#          satisfies exactly as a comment satisfies a token floor.
#
#          The sharpest hollow shape HERE is a test that hand-builds an ActionRun with a digest on it and
#          asserts the journaller copied it. That proves the journaller and says nothing about
#          ActionRun.FromPrompt, which is where the datum is dropped today - the exact way
#          AttemptRecord.Usage shipped structurally dead with every guardrail green (#475). The prompt
#          pins a real serial run whose only fake is a stub IPromptRunner, so PromptResult -> ActionRun ->
#          the provenance fold -> the journal is exercised whole.
#
# TWO DECLARED EXEMPTIONS, stated here because the census's own failure text points a retry agent back at
#          this header:
#
#          1. 'ADigestlessActionRun_LeavesTheProvenanceDigestNull' asserts the ABSENCE case. Nothing
#             populates AttemptProvenance.ModelDigest on today's tree, so a CORRECT test for the null
#             case is GREEN before the fold lands. Its job is to STAY green through task 10, which edits
#             this exact fold and could fill an absent digest with "" or a placeholder.
#
#          2. 'TheDigestRidesTheProvenance_SoItReachesBothSettlePaths' reads the two record shapes by
#             REFLECTION. Task 03 already declared ModelDigest on AttemptProvenance and deliberately not
#             on AttemptRecord, so the assertion already holds and a CORRECT test is GREEN. Its job is to
#             stop the member being re-hung off AttemptRecord later, where it would land in serial mode
#             and silently vanish in worktree mode (JournalModel.cs, grep 'Placement is D32') - worktree
#             being the DEFAULT.
#
#          Demanding red for either would demand a correct test fail. Both rows therefore assert
#          Expect='Executed' (they ran, and were not [Skip]ped) and stay IN the manifest: a dropped row
#          and an oversight look identical.
#
#          The other two rows are red on today's tree because ActionRun.FromPrompt copies no digest and
#          the observed-model fold does not carry one, so any test that asserts a digest ARRIVED on the
#          journalled provenance must fail until task 10 lands.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The four names below were read side by side with this task's
#          action.prompt.md table, which pins each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend on
#          it - kept anyway so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair's OWN test class, never a plan-wide trait (#455). This plan introduces no trait at all, so
# this is shape 3 - the class term alone. 'ModelDigestProvenanceTests' was checked against all 195
# existing Core test class names and every other class this plan authors: it is a substring of none of
# them (in particular ModelDigestCaptureTests, task 07's class, is a DIFFERENT string and neither
# contains the other), so the filter is discriminating.
$filter = 'FullyQualifiedName~ModelDigestProvenanceTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'a reported digest lands on the journalled provenance'          = 'AnActionRunCarryingADigest_LandsItOnTheProvenance'
    'the digest survives the observed-model fold'                   = 'TheDigestSurvivesBesideTheObservedModelFold'
    # DECLARED EXEMPTION 1 - see this file's header. The absence case is already true on a tree where
    # nothing populates the digest, so a CORRECT test is green. Assert it RAN, never that it failed.
    'a digestless run leaves the provenance digest null'            = @{ Name = 'ADigestlessActionRun_LeavesTheProvenanceDigestNull'; Expect = 'Executed' }
    # DECLARED EXEMPTION 2 - see this file's header. Reflection over shapes task 03 already landed, so a
    # CORRECT test is green.
    'the digest rides the provenance, not the attempt record'       = @{ Name = 'TheDigestRidesTheProvenance_SoItReachesBothSettlePaths'; Expect = 'Executed' }
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
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct test is green before the fold lands) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on today's tree, not Failed. ActionRun.FromPrompt copies no digest and the observed-model fold does not carry one, so a test that asserts a digest REACHED the journalled provenance cannot pass - which means this one asserts on a hand-built ActionRun or provenance object instead of on the journal a real serial run produced. Run the executor with a stub IPromptRunner and assert on RunJournal.Document. ('NotExecuted' = [Fact(Skip=...)].)"
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
