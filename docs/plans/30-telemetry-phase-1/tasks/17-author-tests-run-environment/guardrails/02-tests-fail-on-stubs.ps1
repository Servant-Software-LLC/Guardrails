# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a RunEnvironment the test itself constructed, any assertion that never calls Probe). It
#          PASSES against the NotImplementedException stub and hides behind its genuinely-failing
#          siblings, so a suite-level non-zero exit would certify the file honest (#375). One entry per
#          enumerated behaviour, each observed Failed in the runner's OWN TRX - never merely discovered
#          by name, which a hollow body satisfies exactly as a comment satisfies a token floor.
#
#          The hollow shape is unusually tempting on this pair, because three of the four facts
#          (host, OS, CPU count, memory) are trivially obtainable inside the test itself: a test that
#          reads Environment.MachineName and asserts it equals Environment.MachineName is green,
#          reads as coverage, and asserts nothing about the probe at all.
#
# NO EXEMPTIONS. The stub throws NotImplementedException unconditionally, so every one of the four
#          behaviours is red when its test is correct - there is no "already true before the
#          implementation lands" row here, unlike the pairs in this plan whose subject member merely
#          exists unpopulated.
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
# this is shape 3 - the class term alone. 'RunEnvironmentTests' was checked against all 195 existing Core
# test class names and every other class this plan authors: it is a substring of none of them, and none
# of them is a substring of it, so the filter is discriminating.
$filter = 'FullyQualifiedName~RunEnvironmentTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'the probe records host, OS and CPU count'                     = 'TheProbeRecordsHostOsAndCpuCount'
    'the probe records total memory (the unified-memory figure)'   = 'TheProbeRecordsTotalMemory_ForTheUnifiedMemoryComparison'
    'the probe records the concurrency it is GIVEN, not the cores' = 'TheProbeRecordsTheEffectiveConcurrency_NotTheConfiguredOne'
    'the probe records the versions given and nulls the rest'      = 'TheProbeRecordsTheVersionsItIsGiven_AndNullsItIsNotGiven'
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
        $failures += "$behaviour -> '$name' is $seen on the STUB tree, not Failed. RunEnvironmentProbe.Probe throws NotImplementedException unconditionally, so a test that CALLS it cannot pass. Green here means the test never called it - most likely it read Environment.MachineName, Environment.ProcessorCount or GC.GetGCMemoryInfo() itself and asserted about its own value, which passes today and forever. Call RunEnvironmentProbe.Probe and assert on the record it returns. ('NotExecuted' = [Fact(Skip=...)].)"
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
