# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, any assertion that never invokes the subject). It
#          PASSES against the NotImplementedException stubs and hides behind its genuinely-failing
#          siblings, so a suite-level non-zero exit certifies the file honest (#375). One entry per
#          enumerated behaviour in this task's action prompt, each observed Failed in the runner's OWN
#          TRX - never merely discovered by name, which a hollow body satisfies.
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so unlike dotnet.md 4.3 the
# guard does not depend on it - keep it anyway so the logged summary is readable and the pair stays
# copy-pasteable. NO -v q anywhere: pointless here (nothing is re-emitted) and it propagates onto
# forward checks by cloning (#462).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=PlanSourceProvenance&FullyQualifiedName~DeclaredCountGateTests'   # SAME string as the pair's forward half (task 04)

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
# Cross-checked by hand against tasks/03-author-tests-declared-count-gate/action.prompt.md - the
# prompt<->manifest agreement is NOT mechanically enforced (measured: validate exits 0 either way).
$manifest = [ordered]@{
    'N=2, folder records 1 -> FAIL'                          = 'FailsWhenTheFolderRecordsFewerThanTheDeclaredCount'
    'N=2, folder records 3 -> FAIL (M != N, both directions)' = 'FailsWhenTheFolderRecordsMoreThanTheDeclaredCount'
    'N=1, NO decisions.md at all (M=0) -> FAIL'              = 'FailsWhenNoDecisionsFileExistsAndThePlanDeclaresOne'
    'N=2, folder records 2 -> PASS'                          = 'PassesWhenTheRecordedCountEqualsTheDeclaredCount'
    'N=0 -> PASS regardless of what the folder records'      = 'PassesWhenThePlanDeclaresZero'
    'M counts ## DECISION SECTIONS, not lines or existence'  = 'CountsOneDecisionPerSectionInTheDecisionsFile'
    'the failure text names BOTH counts'                     = 'FailureMessageNamesTheDeclaredAndRecordedCounts'
    'the failure text states BOTH limits of the check'       = 'FailureMessageStatesBothLimitsOfTheCheck'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
# start, wrong project path, malformed --filter which exits 0 SILENTLY). Diagnose THAT. Falling through
# would print "every behaviour unbound", a confident wrong message aimed at the one artifact the retry
# agent is allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult)
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
        $failures += "$behaviour -> '$name' is $seen on the STUB tree, not Failed. A test that does not fail against a NotImplementedException stub never invokes the subject, so it asserts a tautology and certifies nothing. Drive the real API and assert the outcome. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the stubs ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
