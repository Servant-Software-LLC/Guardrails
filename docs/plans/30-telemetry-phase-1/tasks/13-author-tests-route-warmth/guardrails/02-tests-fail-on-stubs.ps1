# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          about an AttemptProvenance the test itself constructed, any assertion that never obtains a
#          provenance from the executor). It PASSES on the pre-implementation tree and hides behind its
#          genuinely-failing siblings, so a suite-level non-zero exit would certify the file honest
#          (#375). One entry per enumerated behaviour, each observed Failed in the runner's OWN TRX -
#          never merely discovered by name, which a hollow body satisfies exactly as a comment satisfies
#          a token floor.
#
#          "on stubs" is this plan's file name for the pre-implementation tree. THIS task writes no stub:
#          AttemptProvenance.RouteWarm already exists (03-extend-the-journal-record-shape declared it)
#          and simply nobody sets it, so the red is a RUNTIME red - a correct test asks for the value and
#          gets null. That is a weaker red than a throwing stub, which is precisely why the census below
#          is per-test rather than suite-level: with a throwing stub every honest test is red for free,
#          and here each one has to earn it.
#
# TWO DECLARED EXEMPTIONS, stated here because the census's own failure text points a retry agent back at
#          this header:
#            'AScriptActionWithNoRoute_RecordsNoWarmth' - nothing populates RouteWarm today, so a script
#              attempt's warmth is ALREADY absent and a CORRECT test is green before the flag lands.
#              Demanding red there would demand a correct test fail. It stays in the manifest because it
#              is the clause that stops the next task recording `false` for work that invoked no model at
#              all - a zero in a column an analysis averages.
#            'WarmthRidesTheProvenance_SoItReachesBothSettlePaths' - reflection over the record shape.
#              Task 03 already declared RouteWarm on AttemptProvenance and nowhere else, so a correct test
#              is green on this tree. It stays because it is the clause that stops a later refactor moving
#              the member onto AttemptRecord, where the DEFAULT worktree settle would silently drop it
#              (JournalModel.cs documents that failure - grep 'A member hung directly off the attempt
#              record').
#          Both assert Expect='Executed' (they RAN, and were not [Skip]ped) and stay IN the manifest: a
#          dropped row and an oversight look identical.
#
#          The other three rows obtain a provenance from a real TaskExecutor and assert on RouteWarm,
#          which nothing sets on this tree - so a correct test is red for all three.
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
# this is shape 3 - the class term alone. 'RouteWarmthTests' was checked against all 195 existing Core
# test class names and every other class this plan authors: it is a substring of none of them, and none
# of them is a substring of it, so the filter is discriminating.
$filter = 'FullyQualifiedName~RouteWarmthTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'the first attempt on a (runner, model) route is cold'         = 'TheFirstAttemptOnARoute_IsCold'
    'a second attempt on the SAME route is warm'                   = 'ASecondAttemptOnTheSameRoute_IsWarm'
    'a different model on the same runner is cold again'           = 'ADifferentModelOnTheSameRunner_IsColdAgain'
    # DECLARED EXEMPTION - see this file's header. Nothing sets RouteWarm today, so a script attempt's
    # warmth is already absent and a CORRECT test is green. Assert it RAN, never that it failed.
    'a script action with no route records no warmth' = @{ Name = 'AScriptActionWithNoRoute_RecordsNoWarmth'; Expect = 'Executed' }
    # DECLARED EXEMPTION - see this file's header. Reflection over the record shape; task 03 already put
    # RouteWarm on AttemptProvenance and nowhere else, so a CORRECT test is green. Assert it RAN.
    'warmth rides the provenance, not the record (reflection)' = @{ Name = 'WarmthRidesTheProvenance_SoItReachesBothSettlePaths'; Expect = 'Executed' }
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
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct test is green before the flag lands) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the pre-implementation tree, not Failed. Nothing sets AttemptProvenance.RouteWarm yet, so a test that OBTAINS a provenance from a real TaskExecutor and asserts on its RouteWarm value cannot pass. Green here means the test never obtained one - most likely it constructed an AttemptProvenance itself and asserted about the object it just built, which passes today and forever. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the pre-implementation tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Reminder: the definition of warm/cold is the BREAKDOWN's, not the plan's - section 3.4 names the flag and does not define it. If a failure here is because you disagree with the definition, do NOT re-define it: escalate with kind 'blocked-work'."
    exit 1
}

Write-Output "Per-test red census: all $($manifest.Count) enumerated behaviours are bound to a pinned test, three observed Failed on the pre-implementation tree and two declared exemptions observed Executed."
exit 0
